using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

namespace BreadTranslator.Unity.Editor
{
    public static class AssetTableScanner
    {
        public static AssetTableScanReport ScanProject(string sourceLocaleCode = null)
        {
            var locale = ResolveLocale(sourceLocaleCode);
            if (locale == null)
            {
                return new AssetTableScanReport(0, sourceLocaleCode ?? "", Array.Empty<AssetTableRecord>(),
                    new[] { "No Unity Localization locale is configured. Add a source locale before scanning Asset Tables." });
            }

            return ScanCollections(LocalizationEditorSettings.GetAssetTableCollections(), locale.Identifier);
        }

        public static AssetTableScanReport ScanCollections(
            IEnumerable<AssetTableCollection> collections,
            LocaleIdentifier sourceLocale)
        {
            var collectionList = (collections ?? Array.Empty<AssetTableCollection>())
                .Where(collection => collection != null && collection.SharedData != null)
                .OrderBy(collection => collection.SharedData.TableCollectionNameGuid.ToString(), StringComparer.Ordinal)
                .ThenBy(collection => collection.TableCollectionName, StringComparer.Ordinal)
                .ToArray();
            var records = new List<AssetTableRecord>();
            var warnings = new List<string>();

            foreach (var collection in collectionList)
            {
                var collectionGuid = collection.SharedData.TableCollectionNameGuid.ToString();
                var sourceTable = collection.GetTable(sourceLocale) as AssetTable;
                if (sourceTable == null)
                {
                    warnings.Add($"Asset collection '{collection.TableCollectionName}' has no table for source locale {sourceLocale.Code}.");
                    continue;
                }

                var tablePath = AssetDatabase.GetAssetPath(collection);
                foreach (var sharedEntry in collection.SharedData.Entries.OrderBy(entry => entry.Id))
                {
                    var localizedEntry = sourceTable.GetEntry(sharedEntry.Id);
                    if (localizedEntry == null)
                    {
                        warnings.Add($"Asset collection '{collection.TableCollectionName}' is missing source entry {sharedEntry.Id} for {sourceLocale.Code}.");
                        continue;
                    }

                    records.Add(new AssetTableRecord(
                        collectionGuid,
                        collection.TableCollectionName,
                        tablePath,
                        sharedEntry.Id.ToString(),
                        sharedEntry.Key,
                        sourceLocale.Code,
                        localizedEntry.Guid));
                }
            }

            return new AssetTableScanReport(collectionList.Length, sourceLocale.Code, records, warnings);
        }

        private static Locale ResolveLocale(string sourceLocaleCode)
        {
            var locales = LocalizationEditorSettings.GetLocales();
            if (!string.IsNullOrWhiteSpace(sourceLocaleCode))
            {
                return locales.FirstOrDefault(locale => string.Equals(locale.Identifier.Code, sourceLocaleCode, StringComparison.OrdinalIgnoreCase));
            }
            return locales.FirstOrDefault();
        }
    }

    public sealed class AssetTableScanReport
    {
        public AssetTableScanReport(
            int collectionCount,
            string sourceLocale,
            IEnumerable<AssetTableRecord> records,
            IEnumerable<string> warnings)
        {
            CollectionCount = collectionCount;
            SourceLocale = sourceLocale ?? "";
            Records = (records ?? Array.Empty<AssetTableRecord>())
                .Where(record => record != null)
                .GroupBy(record => record.StableId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(record => record.TableCollectionGuid, StringComparer.Ordinal)
                .ThenBy(record => record.EntryIdValue)
                .ThenBy(record => record.Key, StringComparer.Ordinal)
                .ToArray();
            Warnings = (warnings ?? Array.Empty<string>())
                .Where(warning => !string.IsNullOrWhiteSpace(warning))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(warning => warning, StringComparer.Ordinal)
                .ToArray();
        }

        public int CollectionCount { get; }
        public int EntryCount => Records.Count;
        public string SourceLocale { get; }
        public IReadOnlyList<AssetTableRecord> Records { get; }
        public IReadOnlyList<string> Warnings { get; }
    }

    public sealed class AssetTableRecord
    {
        public AssetTableRecord(
            string tableCollectionGuid,
            string tableCollectionName,
            string tablePath,
            string entryId,
            string key,
            string sourceLocale,
            string assetGuid)
        {
            TableCollectionGuid = tableCollectionGuid ?? "";
            TableCollectionName = tableCollectionName ?? "";
            TablePath = tablePath ?? "";
            EntryId = entryId ?? "";
            Key = key ?? "";
            SourceLocale = sourceLocale ?? "";
            AssetGuid = assetGuid ?? "";
        }

        public string StableId => $"unity-asset:{TableCollectionGuid}:{EntryId}";
        internal long EntryIdValue => long.TryParse(EntryId, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : long.MaxValue;
        public string TableCollectionGuid { get; }
        public string TableCollectionName { get; }
        public string TablePath { get; }
        public string EntryId { get; }
        public string Key { get; }
        public string SourceLocale { get; }
        public string AssetGuid { get; }
    }
}
