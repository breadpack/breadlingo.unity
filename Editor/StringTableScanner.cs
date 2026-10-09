using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

namespace BreadTranslator.Unity.Editor
{
    public static class StringTableScanner
    {
        private static readonly Regex PlaceholderPattern = new Regex(@"\{[^{}\s][^{}]*\}", RegexOptions.Compiled);
        private static readonly Regex RichTextPattern = new Regex(@"</?[a-zA-Z][^>]*>", RegexOptions.Compiled);

        public static StringTableScanReport ScanProject(string sourceLocaleCode = null)
        {
            var records = new List<StringTableRecord>();
            var warnings = new List<string>();
            var locale = ResolveLocale(sourceLocaleCode);
            if (locale == null)
            {
                warnings.Add("No Unity Localization locale is configured. Add a source locale before scanning String Tables.");
                return new StringTableScanReport(0, 0, sourceLocaleCode ?? "", records, warnings);
            }

            var collections = LocalizationEditorSettings.GetStringTableCollections();
            foreach (var collection in collections.OrderBy(item => item.TableCollectionName, StringComparer.Ordinal))
            {
                var sourceTable = collection.GetTable(locale.Identifier) as StringTable;
                if (sourceTable == null)
                {
                    warnings.Add($"Collection '{collection.TableCollectionName}' has no table for source locale {locale.Identifier.Code}.");
                    continue;
                }

                var tablePath = AssetDatabase.GetAssetPath(collection);
                var collectionGuid = collection.SharedData.TableCollectionNameGuid.ToString();
                foreach (var sharedEntry in collection.SharedData.Entries.OrderBy(entry => entry.Id))
                {
                    var localizedEntry = sourceTable.GetEntry(sharedEntry.Id);
                    if (localizedEntry == null)
                    {
                        warnings.Add($"Collection '{collection.TableCollectionName}' is missing source entry {sharedEntry.Id} for {locale.Identifier.Code}.");
                        continue;
                    }

                    var sourceText = localizedEntry.Value ?? "";
                    var metadata = ExtractMetadata(localizedEntry);
                    records.Add(new StringTableRecord(
                        collectionGuid,
                        collection.TableCollectionName,
                        tablePath,
                        sharedEntry.Id.ToString(),
                        sharedEntry.Key,
                        locale.Identifier.Code,
                        sourceText,
                        localizedEntry.IsSmart,
                        UniqueMatches(PlaceholderPattern, sourceText),
                        UniqueMatches(RichTextPattern, sourceText),
                        metadata));
                }
            }

            return new StringTableScanReport(collections.Count, records.Count, locale.Identifier.Code, records, warnings);
        }

        private static UnityEngine.Localization.Locale ResolveLocale(string sourceLocaleCode)
        {
            var locales = LocalizationEditorSettings.GetLocales();
            if (!string.IsNullOrWhiteSpace(sourceLocaleCode))
            {
                return locales.FirstOrDefault(locale => string.Equals(locale.Identifier.Code, sourceLocaleCode, StringComparison.OrdinalIgnoreCase));
            }
            return locales.FirstOrDefault();
        }

        private static IReadOnlyList<string> UniqueMatches(Regex pattern, string value)
        {
            return pattern.Matches(value ?? "")
                .Cast<Match>()
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
        }

        private static IReadOnlyDictionary<string, string> ExtractMetadata(params object[] metadataCollections)
        {
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var collection in metadataCollections)
            {
                if (collection == null) continue;
                var entriesProperty = collection.GetType().GetProperty("MetadataEntries", BindingFlags.Instance | BindingFlags.Public);
                if (!(entriesProperty?.GetValue(collection) is IEnumerable entries)) continue;
                foreach (var entry in entries)
                {
                    if (entry == null) continue;
                    var key = entry.GetType().FullName ?? entry.GetType().Name;
                    var value = entry.ToString() ?? "";
                    result[key] = value;
                }
            }
            return result;
        }
    }

    public sealed class StringTableScanReport
    {
        public StringTableScanReport(int tableCount, int entryCount, string sourceLocale, IReadOnlyList<StringTableRecord> records, IReadOnlyList<string> warnings)
        {
            TableCount = tableCount;
            EntryCount = entryCount;
            SourceLocale = sourceLocale ?? "";
            Records = records ?? Array.Empty<StringTableRecord>();
            Warnings = warnings ?? Array.Empty<string>();
        }

        public int TableCount { get; }
        public int EntryCount { get; }
        public string SourceLocale { get; }
        public IReadOnlyList<StringTableRecord> Records { get; }
        public IReadOnlyList<string> Warnings { get; }
    }

    public sealed class StringTableRecord
    {
        public StringTableRecord(
            string tableCollectionGuid,
            string tableCollectionName,
            string tablePath,
            string entryId,
            string key,
            string sourceLocale,
            string sourceText,
            bool smartString,
            IReadOnlyList<string> placeholders,
            IReadOnlyList<string> richTextTags,
            IReadOnlyDictionary<string, string> metadata)
        {
            TableCollectionGuid = tableCollectionGuid ?? "";
            TableCollectionName = tableCollectionName ?? "";
            TablePath = tablePath ?? "";
            EntryId = entryId ?? "";
            Key = key ?? "";
            SourceLocale = sourceLocale ?? "";
            SourceText = sourceText ?? "";
            SmartString = smartString;
            Placeholders = placeholders ?? Array.Empty<string>();
            RichTextTags = richTextTags ?? Array.Empty<string>();
            Metadata = metadata ?? new Dictionary<string, string>();
        }

        public string StableId => $"unity:{TableCollectionGuid}:{EntryId}";
        public string TableCollectionGuid { get; }
        public string TableCollectionName { get; }
        public string TablePath { get; }
        public string EntryId { get; }
        public string Key { get; }
        public string SourceLocale { get; }
        public string SourceText { get; }
        public bool SmartString { get; }
        public IReadOnlyList<string> Placeholders { get; }
        public IReadOnlyList<string> RichTextTags { get; }
        public IReadOnlyDictionary<string, string> Metadata { get; }
    }
}
