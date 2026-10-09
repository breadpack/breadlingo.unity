using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

namespace BreadTranslator.Unity.Editor
{
    public static class CsvAdapter
    {
        private static readonly string[] Header = { "stable_id", "table_guid", "entry_id", "key", "source_locale", "target_locale", "source", "target" };

        public static string ExportCsv(StringTableScanReport report, string targetLocale)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (string.IsNullOrWhiteSpace(targetLocale)) throw new ArgumentException("Target locale is required.", nameof(targetLocale));
            var output = new StringBuilder();
            WriteRow(output, Header);
            foreach (var record in report.Records.OrderBy(item => item.StableId, StringComparer.Ordinal))
            {
                WriteRow(output, new[] { record.StableId, record.TableCollectionGuid, record.EntryId, record.Key, record.SourceLocale, targetLocale, record.SourceText, "" });
            }
            return output.ToString();
        }

        public static CsvApplyReport ApplyCsv(string csv, string targetLocaleOverride = null)
        {
            ParsedCsv parsed;
            try { parsed = Parse(csv); }
            catch (FormatException error) { return CsvApplyReport.Blocked("invalid_csv", error.Message); }
            var conflicts = new List<CsvApplyConflict>();
            var changes = new List<Tuple<StringTable, StringTableEntry, string>>();
            var unchanged = 0;
            var collections = LocalizationEditorSettings.GetStringTableCollections()
                .ToDictionary(item => item.SharedData.TableCollectionNameGuid.ToString(), StringComparer.Ordinal);

            foreach (var row in parsed.Rows)
            {
                var targetLocale = targetLocaleOverride ?? row.TargetLocale;
                if (string.Equals(row.SourceLocale, targetLocale, StringComparison.OrdinalIgnoreCase)) { conflicts.Add(new CsvApplyConflict(row.StableId, "source_target_same")); continue; }
                if (!string.Equals(targetLocale, row.TargetLocale, StringComparison.OrdinalIgnoreCase)) { conflicts.Add(new CsvApplyConflict(row.StableId, "artifact_locale_mismatch")); continue; }
                if (!collections.TryGetValue(row.TableGuid, out var collection)) { conflicts.Add(new CsvApplyConflict(row.StableId, "table_missing")); continue; }
                if (!long.TryParse(row.EntryId, NumberStyles.None, CultureInfo.InvariantCulture, out var entryId)) { conflicts.Add(new CsvApplyConflict(row.StableId, "entry_id_invalid")); continue; }
                var sourceTable = collection.GetTable(new LocaleIdentifier(row.SourceLocale)) as StringTable;
                var targetTable = collection.GetTable(new LocaleIdentifier(targetLocale)) as StringTable;
                var shared = collection.SharedData.GetEntry(entryId);
                var sourceEntry = sourceTable?.GetEntry(entryId);
                var targetEntry = targetTable?.GetEntry(entryId);
                if (sourceTable == null || targetTable == null) { conflicts.Add(new CsvApplyConflict(row.StableId, "locale_table_missing")); continue; }
                if (shared == null || sourceEntry == null || targetEntry == null) { conflicts.Add(new CsvApplyConflict(row.StableId, "entry_missing")); continue; }
                if (!string.Equals(NeutralizeSpreadsheetFormula(shared.Key), row.Key, StringComparison.Ordinal)) { conflicts.Add(new CsvApplyConflict(row.StableId, "key_mismatch")); continue; }
                if (!string.Equals(NeutralizeSpreadsheetFormula(sourceEntry.Value ?? ""), row.Source, StringComparison.Ordinal)) { conflicts.Add(new CsvApplyConflict(row.StableId, "source_changed")); continue; }
                if (string.IsNullOrEmpty(row.Target)) { unchanged++; continue; }
                var missing = StringTableScannerPlaceholderReader.Read(row.Source).FirstOrDefault(token => !(row.Target ?? "").Contains(token));
                if (missing != null) { conflicts.Add(new CsvApplyConflict(row.StableId, "placeholder_missing")); continue; }
                if (string.Equals(targetEntry.Value ?? "", row.Target ?? "", StringComparison.Ordinal)) unchanged++;
                else changes.Add(Tuple.Create(targetTable, targetEntry, row.Target ?? ""));
            }
            if (conflicts.Count > 0) return new CsvApplyReport(0, unchanged, conflicts);
            foreach (var change in changes) { Undo.RecordObject(change.Item1, "Apply BreadTranslator CSV"); change.Item2.Value = change.Item3; EditorUtility.SetDirty(change.Item1); }
            if (changes.Count > 0) AssetDatabase.SaveAssets();
            return new CsvApplyReport(changes.Count, unchanged, conflicts);
        }

        public static ParsedCsv Parse(string csv)
        {
            var rows = ReadRows(csv ?? "");
            if (rows.Count == 0 || !rows[0].SequenceEqual(Header, StringComparer.Ordinal)) throw new FormatException("CSV header does not match the BreadTranslator Unity contract.");
            var result = new List<CsvRow>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var values in rows.Skip(1))
            {
                if (values.Count == 1 && values[0].Length == 0) continue;
                if (values.Count != Header.Length) throw new FormatException("CSV row has an invalid column count.");
                var row = new CsvRow(values);
                if (string.IsNullOrWhiteSpace(row.StableId) || row.StableId != $"unity:{row.TableGuid}:{row.EntryId}") throw new FormatException("CSV stable identity is invalid.");
                if (string.IsNullOrWhiteSpace(row.SourceLocale) || string.IsNullOrWhiteSpace(row.TargetLocale)) throw new FormatException("CSV locale is missing.");
                if (!identities.Add(row.StableId)) throw new FormatException("CSV contains duplicate stable identities.");
                result.Add(row);
            }
            return new ParsedCsv(result);
        }

        private static void WriteRow(StringBuilder output, IEnumerable<string> values) => output.AppendLine(string.Join(",", values.Select(Escape)));
        private static string Escape(string value)
        {
            value = NeutralizeSpreadsheetFormula(value ?? "");
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        }
        private static string NeutralizeSpreadsheetFormula(string value)
        {
            var index = 0;
            while (index < value.Length && (char.IsWhiteSpace(value[index]) || char.IsControl(value[index]))) index++;
            return index < value.Length && "=+-@".IndexOf(value[index]) >= 0 ? $"'{value}" : value;
        }
        private static List<List<string>> ReadRows(string value)
        {
            var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (quoted && c == '"' && i + 1 < value.Length && value[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = !quoted;
                else if (!quoted && c == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if (!quoted && (c == '\r' || c == '\n')) { if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n') i++; row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); }
                else cell.Append(c);
            }
            if (quoted) throw new FormatException("CSV contains an unterminated quoted field.");
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
            return rows;
        }
    }

    public sealed class ParsedCsv { public ParsedCsv(IReadOnlyList<CsvRow> rows) { Rows = rows; } public IReadOnlyList<CsvRow> Rows { get; } }
    public sealed class CsvRow
    {
        public CsvRow(IReadOnlyList<string> values) { StableId=values[0]; TableGuid=values[1]; EntryId=values[2]; Key=values[3]; SourceLocale=values[4]; TargetLocale=values[5]; Source=values[6]; Target=values[7]; }
        public string StableId { get; } public string TableGuid { get; } public string EntryId { get; } public string Key { get; } public string SourceLocale { get; } public string TargetLocale { get; } public string Source { get; } public string Target { get; }
    }
    public sealed class CsvApplyConflict { public CsvApplyConflict(string stableId, string code) { StableId=stableId; Code=code; } public string StableId { get; } public string Code { get; } }
    public sealed class CsvApplyReport
    {
        public CsvApplyReport(int applied, int unchanged, IReadOnlyList<CsvApplyConflict> conflicts) { Applied=applied; Unchanged=unchanged; Conflicts=conflicts ?? Array.Empty<CsvApplyConflict>(); }
        public int Applied { get; } public int Unchanged { get; } public IReadOnlyList<CsvApplyConflict> Conflicts { get; } public bool HasConflicts => Conflicts.Count > 0;
        public static CsvApplyReport Blocked(string code, string message) => new CsvApplyReport(0, 0, new[] { new CsvApplyConflict("", code) });
    }
}
