using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

namespace BreadTranslator.Unity.Editor
{
    public static class XliffAdapter
    {
        private static readonly XNamespace Xliff12 = "urn:oasis:names:tc:xliff:document:1.2";
        private static readonly XNamespace Xliff20 = "urn:oasis:names:tc:xliff:document:2.0";
        private static readonly XNamespace BreadTranslator = "urn:breadtranslator:unity:1";
        private const int MaxXliffCharacters = 16 * 1024 * 1024;

        public static string BuildXliff12(StringTableScanReport report, string targetLocale)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            var body = new XElement(Xliff12 + "body", report.Records.Select(record =>
                new XElement(Xliff12 + "trans-unit",
                    new XAttribute("id", record.EntryId),
                    new XAttribute("resname", record.Key),
                    new XAttribute(BreadTranslator + "stable-id", record.StableId),
                    new XAttribute(BreadTranslator + "table-guid", record.TableCollectionGuid),
                    new XAttribute(BreadTranslator + "table-name", record.TableCollectionName),
                    new XElement(Xliff12 + "source", record.SourceText),
                    new XElement(Xliff12 + "target", ""),
                    new XElement(Xliff12 + "note", new XAttribute("from", "breadtranslator"), record.TablePath))));
            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(Xliff12 + "xliff",
                    new XAttribute(XNamespace.Xmlns + "bt", BreadTranslator),
                    new XAttribute("version", "1.2"),
                    new XElement(Xliff12 + "file",
                        new XAttribute("original", "unity-string-tables"),
                        new XAttribute("datatype", "plaintext"),
                        new XAttribute("source-language", report.SourceLocale),
                        new XAttribute("target-language", targetLocale ?? ""),
                        body)));
            return Serialize(document);
        }

        public static string BuildXliff20(StringTableScanReport report, string targetLocale)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            var file = new XElement(Xliff20 + "file", new XAttribute("id", "unity-string-tables"),
                report.Records.Select(record =>
                    new XElement(Xliff20 + "unit",
                        new XAttribute("id", record.EntryId),
                        new XAttribute("name", record.Key),
                        new XAttribute(BreadTranslator + "stable-id", record.StableId),
                        new XAttribute(BreadTranslator + "table-guid", record.TableCollectionGuid),
                        new XAttribute(BreadTranslator + "table-name", record.TableCollectionName),
                        new XElement(Xliff20 + "notes", new XElement(Xliff20 + "note", record.TablePath)),
                        new XElement(Xliff20 + "segment",
                            new XElement(Xliff20 + "source", record.SourceText),
                            new XElement(Xliff20 + "target", "")))));
            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(Xliff20 + "xliff",
                    new XAttribute(XNamespace.Xmlns + "bt", BreadTranslator),
                    new XAttribute("version", "2.0"),
                    new XAttribute("srcLang", report.SourceLocale),
                    new XAttribute("trgLang", targetLocale ?? ""),
                    file));
            return Serialize(document);
        }

        public static XliffApplyReport Apply(string xliff, string targetLocaleOverride = null, bool previewOnly = false, IReadOnlyDictionary<string, string> expectedTargets = null)
        {
            var parsed = Parse(xliff);
            try { UnityArtifactVerifier.ValidateIdentities(parsed); }
            catch (UnityArtifactVerificationException error)
            {
                return XliffApplyReport.Blocked(error.Code, error.Message);
            }
            var targetLocale = string.IsNullOrWhiteSpace(targetLocaleOverride) ? parsed.TargetLocale : targetLocaleOverride;
            if (string.Equals(parsed.SourceLocale, targetLocale, StringComparison.OrdinalIgnoreCase))
                return XliffApplyReport.Blocked("source_target_same", "Source and target locales must be different.");
            if (string.IsNullOrWhiteSpace(parsed.SourceLocale) || string.IsNullOrWhiteSpace(targetLocale))
            {
                return XliffApplyReport.Blocked("artifact_locale_mismatch", "XLIFF source or target locale is missing.");
            }
            if (!string.IsNullOrWhiteSpace(targetLocaleOverride) && !string.Equals(parsed.TargetLocale, targetLocaleOverride, StringComparison.Ordinal))
            {
                return XliffApplyReport.Blocked("artifact_locale_mismatch", "XLIFF target locale does not match the requested locale.");
            }

            var collections = LocalizationEditorSettings.GetStringTableCollections()
                .ToDictionary(collection => collection.SharedData.TableCollectionNameGuid.ToString(), StringComparer.OrdinalIgnoreCase);
            var conflicts = new List<XliffApplyConflict>();
            var changes = new List<Tuple<StringTable, StringTableEntry, StringTableEntry, string>>();
            var unchanged = 0;

            foreach (var unit in parsed.Units)
            {
                if (!collections.TryGetValue(unit.TableCollectionGuid, out var collection))
                {
                    conflicts.Add(new XliffApplyConflict(unit.StableId, "table_missing", "String Table Collection was not found."));
                    continue;
                }
                if (!long.TryParse(unit.EntryId, out var entryId))
                {
                    conflicts.Add(new XliffApplyConflict(unit.StableId, "entry_id_invalid", "Unity entry ID is not a valid Int64 value."));
                    continue;
                }

                var sourceTable = collection.GetTable(new LocaleIdentifier(parsed.SourceLocale)) as StringTable;
                var targetTable = collection.GetTable(new LocaleIdentifier(targetLocale)) as StringTable;
                if (sourceTable == null || targetTable == null)
                {
                    conflicts.Add(new XliffApplyConflict(unit.StableId, "locale_table_missing", $"Source or target locale table is missing ({parsed.SourceLocale} -> {targetLocale})."));
                    continue;
                }
                var sourceEntry = sourceTable.GetEntry(entryId);
                var targetEntry = targetTable.GetEntry(entryId);
                var sharedEntry = collection.SharedData.GetEntry(entryId);
                if (sourceEntry == null || targetEntry == null || sharedEntry == null)
                {
                    conflicts.Add(new XliffApplyConflict(unit.StableId, "entry_missing", "Entry is missing from shared, source, or target table data."));
                    continue;
                }
                if (!string.Equals(sharedEntry.Key, unit.Key, StringComparison.Ordinal))
                {
                    conflicts.Add(new XliffApplyConflict(unit.StableId, "key_mismatch", $"Current key '{sharedEntry.Key}' does not match XLIFF key '{unit.Key}'."));
                    continue;
                }
                if (!string.Equals(sourceEntry.Value ?? "", unit.SourceText ?? "", StringComparison.Ordinal))
                {
                    conflicts.Add(new XliffApplyConflict(unit.StableId, "source_changed", "Source text changed after XLIFF export."));
                    continue;
                }
                // Export templates contain empty targets; never erase a translation with a blank cell.
                if (string.IsNullOrEmpty(unit.TargetText)) { unchanged += 1; continue; }
                if (expectedTargets != null && (!expectedTargets.TryGetValue(unit.StableId, out var expected) ||
                    !string.Equals(targetEntry.Value ?? "", expected, StringComparison.Ordinal)))
                {
                    conflicts.Add(new XliffApplyConflict(unit.StableId, "local_target_changed", "Local translation changed since preview or pull started. Refresh the preview."));
                    continue;
                }
                var missingPlaceholder = FindMissingPlaceholder(unit.SourceText, unit.TargetText);
                if (missingPlaceholder != null)
                {
                    conflicts.Add(new XliffApplyConflict(unit.StableId, "placeholder_missing", $"Target text is missing placeholder {missingPlaceholder}."));
                    continue;
                }
                if (string.Equals(targetEntry.Value ?? "", unit.TargetText ?? "", StringComparison.Ordinal))
                {
                    unchanged += 1;
                    continue;
                }

                changes.Add(Tuple.Create(targetTable, targetEntry, sourceEntry, unit.TargetText ?? ""));
            }

            if (conflicts.Count > 0) return new XliffApplyReport(0, unchanged, conflicts);
            if (previewOnly) return new XliffApplyReport(changes.Count, unchanged, conflicts);
            foreach (var change in changes)
            {
                Undo.RecordObject(change.Item1, "Apply BreadTranslator XLIFF");
                change.Item2.Value = change.Item4;
                change.Item2.IsSmart = change.Item3.IsSmart;
                EditorUtility.SetDirty(change.Item1);
            }
            if (changes.Count > 0) AssetDatabase.SaveAssets();
            return new XliffApplyReport(changes.Count, unchanged, conflicts);
        }

        public static ParsedXliff Parse(string xliff)
        {
            if (string.IsNullOrWhiteSpace(xliff)) throw new ArgumentException("XLIFF content is required.", nameof(xliff));
            if (xliff.Length > MaxXliffCharacters) throw new FormatException("XLIFF content exceeds the parser limit.");
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxXliffCharacters,
                MaxCharactersFromEntities = 0,
                IgnoreProcessingInstructions = false
            };
            XDocument document;
            using (var textReader = new StringReader(xliff))
            using (var reader = XmlReader.Create(textReader, settings))
            {
                document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            }
            var root = document.Root ?? throw new FormatException("XLIFF root element is missing.");
            var version = (string)root.Attribute("version") ?? "";
            var isV2 = version.StartsWith("2.", StringComparison.Ordinal);
            var expectedNamespace = isV2 ? Xliff20 : Xliff12;
            if (root.Name.LocalName != "xliff" || (version != "1.2" && !isV2) || root.Name.Namespace != expectedNamespace)
            {
                throw new FormatException("XLIFF root version or namespace is invalid.");
            }
            if (document.DescendantNodes().OfType<XProcessingInstruction>().Any())
            {
                throw new FormatException("XLIFF processing instructions are not allowed.");
            }
            if (root.DescendantsAndSelf().Any(IsExternalReference))
            {
                throw new FormatException("XLIFF external references are not allowed.");
            }
            var sourceLocale = isV2
                ? (string)root.Attribute("srcLang") ?? ""
                : (string)root.Descendants().FirstOrDefault(element => element.Name.LocalName == "file")?.Attribute("source-language") ?? "";
            var targetLocale = isV2
                ? (string)root.Attribute("trgLang") ?? ""
                : (string)root.Descendants().FirstOrDefault(element => element.Name.LocalName == "file")?.Attribute("target-language") ?? "";
            var unitElements = root.Descendants().Where(element => element.Name.LocalName == (isV2 ? "unit" : "trans-unit"));
            var units = unitElements.Select(element => ParseUnit(element, isV2)).ToArray();
            return new ParsedXliff(version, sourceLocale, targetLocale, units);
        }

        private static XliffUnit ParseUnit(XElement element, bool isV2)
        {
            var entryId = (string)element.Attribute("id") ?? "";
            var key = (string)element.Attribute(isV2 ? "name" : "resname") ?? "";
            var tableGuid = (string)element.Attribute(BreadTranslator + "table-guid") ?? "";
            var stableId = (string)element.Attribute(BreadTranslator + "stable-id") ?? $"unity:{tableGuid}:{entryId}";
            var sourceElement = element.Descendants().FirstOrDefault(item => item.Name.LocalName == "source");
            var targetElement = element.Descendants().FirstOrDefault(item => item.Name.LocalName == "target");
            var sourceInline = ReadInlineText(sourceElement);
            var targetInline = ReadInlineText(targetElement);
            if (!string.IsNullOrEmpty(targetInline.Text) && !sourceInline.Signatures.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(targetInline.Signatures.OrderBy(value => value, StringComparer.Ordinal)))
            {
                throw new FormatException("XLIFF source and target inline codes do not match.");
            }
            if (string.IsNullOrWhiteSpace(entryId) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(tableGuid))
            {
                throw new FormatException("Each Unity XLIFF unit requires id, key/resname, and bt:table-guid.");
            }
            return new XliffUnit(stableId, tableGuid, entryId, key, sourceInline.Text, targetInline.Text);
        }

        private static bool IsExternalReference(XElement element)
        {
            if (element.Name.NamespaceName == "http://www.w3.org/2001/XInclude" || element.Name.LocalName == "external-file") return true;
            return element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "schemaLocation" ||
                attribute.Name.LocalName == "noNamespaceSchemaLocation" ||
                attribute.Name.LocalName == "base" ||
                ((attribute.Name.LocalName == "href" || attribute.Name.LocalName == "src") && Uri.TryCreate(attribute.Value, UriKind.Absolute, out _)));
        }

        private static InlineText ReadInlineText(XElement container)
        {
            if (container == null) return new InlineText("", Array.Empty<string>());
            var signatures = new List<string>();
            var text = ReadInlineNodes(container.Nodes(), signatures);
            return new InlineText(text, signatures);
        }

        private static string ReadInlineNodes(IEnumerable<XNode> nodes, ICollection<string> signatures)
        {
            return string.Concat(nodes.Select(node =>
            {
                if (node is XText text) return text.Value;
                if (!(node is XElement inline)) throw new FormatException("Unsupported node in XLIFF inline content.");
                var name = inline.Name.LocalName;
                if (!new[] { "x", "ph", "it", "g", "pc", "mrk" }.Contains(name))
                {
                    throw new FormatException($"Unsupported XLIFF inline element '{name}'.");
                }
                var id = InlineAttribute(inline, "id") ?? InlineAttribute(inline, "rid") ?? InlineAttribute(inline, "mid") ?? InlineAttribute(inline, "equiv") ?? InlineAttribute(inline, "equiv-text");
                if (string.IsNullOrWhiteSpace(id)) throw new FormatException($"XLIFF inline element '{name}' requires an identifier.");
                var paired = name == "g" || name == "pc" || name == "mrk";
                signatures.Add((paired ? "paired:" : "standalone:") + name + ":" + id);
                if (!paired)
                {
                    return InlineAttribute(inline, "equiv-text") ?? InlineAttribute(inline, "equiv") ?? "{" + id + "}";
                }
                var content = ReadInlineNodes(inline.Nodes(), signatures);
                return (InlineAttribute(inline, "equivStart") ?? "") + content + (InlineAttribute(inline, "equivEnd") ?? "");
            }));
        }

        private static string InlineAttribute(XElement element, string localName)
        {
            return (string)element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName);
        }

        private sealed class InlineText
        {
            public InlineText(string text, IReadOnlyList<string> signatures) { Text = text; Signatures = signatures; }
            public string Text { get; }
            public IReadOnlyList<string> Signatures { get; }
        }

        private static string FindMissingPlaceholder(string source, string target)
        {
            foreach (var placeholder in StringTableScannerPlaceholderReader.Read(source))
            {
                if (!(target ?? "").Contains(placeholder)) return placeholder;
            }
            return null;
        }

        private static string Serialize(XDocument document)
        {
            return document.Declaration + Environment.NewLine + document.ToString(SaveOptions.DisableFormatting);
        }
    }

    internal static class StringTableScannerPlaceholderReader
    {
        private static readonly System.Text.RegularExpressions.Regex Pattern = new System.Text.RegularExpressions.Regex(@"\{[^{}\s][^{}]*\}");
        public static IEnumerable<string> Read(string value) => Pattern.Matches(value ?? "").Cast<System.Text.RegularExpressions.Match>().Select(match => match.Value).Distinct();
    }

    public sealed class ParsedXliff
    {
        public ParsedXliff(string version, string sourceLocale, string targetLocale, IReadOnlyList<XliffUnit> units)
        {
            Version = version; SourceLocale = sourceLocale; TargetLocale = targetLocale; Units = units;
        }
        public string Version { get; }
        public string SourceLocale { get; }
        public string TargetLocale { get; }
        public IReadOnlyList<XliffUnit> Units { get; }
    }

    public sealed class XliffUnit
    {
        public XliffUnit(string stableId, string tableCollectionGuid, string entryId, string key, string sourceText, string targetText)
        {
            StableId = stableId; TableCollectionGuid = tableCollectionGuid; EntryId = entryId; Key = key; SourceText = sourceText; TargetText = targetText;
        }
        public string StableId { get; }
        public string TableCollectionGuid { get; }
        public string EntryId { get; }
        public string Key { get; }
        public string SourceText { get; }
        public string TargetText { get; }
    }

    public sealed class XliffApplyConflict
    {
        public XliffApplyConflict(string stableId, string code, string message) { StableId = stableId; Code = code; Message = message; }
        public string StableId { get; }
        public string Code { get; }
        public string Message { get; }
    }

    public sealed class XliffApplyReport
    {
        public XliffApplyReport(int applied, int unchanged, IReadOnlyList<XliffApplyConflict> conflicts)
        {
            Applied = applied; Unchanged = unchanged; Conflicts = conflicts ?? Array.Empty<XliffApplyConflict>();
        }
        public int Applied { get; }
        public int Unchanged { get; }
        public IReadOnlyList<XliffApplyConflict> Conflicts { get; }
        public bool HasConflicts => Conflicts.Count > 0;
        public static XliffApplyReport Blocked(string message) => Blocked("invalid_xliff", message);
        public static XliffApplyReport Blocked(string code, string message) => new XliffApplyReport(0, 0, new[] { new XliffApplyConflict("", code, message) });
    }
}
