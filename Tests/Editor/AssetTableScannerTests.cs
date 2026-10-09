using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

namespace BreadTranslator.Unity.Editor.Tests
{
    public sealed class AssetTableScannerTests
    {
        private const string SourceLocale = "en";
        private string assetDirectory;
        private AssetTableCollection collection;
        private AssetTable sourceTable;

        [SetUp]
        public void SetUp()
        {
            assetDirectory = $"Assets/__BreadTranslatorAssetTableScannerTests_{Guid.NewGuid():N}";
            collection = LocalizationEditorSettings.CreateAssetTableCollection(
                $"BreadTranslator Asset Context {Guid.NewGuid():N}",
                assetDirectory,
                new List<UnityEngine.Localization.Locale>());
            sourceTable = (AssetTable)collection.AddNewTable(new LocaleIdentifier(SourceLocale));
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(assetDirectory);
            AssetDatabase.SaveAssets();
        }

        [Test]
        public void ScanUsesStableIdentityAndDeterministicOrderWithoutMetadata()
        {
            sourceTable.AddEntry("ui.icon.quit", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            sourceTable.AddEntry("ui.icon.play", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            EditorUtility.SetDirty(sourceTable);
            EditorUtility.SetDirty(collection.SharedData);
            AssetDatabase.SaveAssets();

            var first = AssetTableScanner.ScanCollections(new[] { collection }, new LocaleIdentifier(SourceLocale));
            var second = AssetTableScanner.ScanCollections(new[] { collection }, new LocaleIdentifier(SourceLocale));

            Assert.That(first.EntryCount, Is.EqualTo(2));
            Assert.That(first.Records.Select(record => record.StableId), Is.EqualTo(second.Records.Select(record => record.StableId)));
            Assert.That(first.Records.Select(record => long.Parse(record.EntryId)), Is.Ordered);
            Assert.That(first.Records.All(record => record.StableId == $"unity-asset:{record.TableCollectionGuid}:{record.EntryId}"), Is.True);
            Assert.That(first.Records.Select(record => record.AssetGuid), Is.EquivalentTo(new[]
            {
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }));
        }
    }
}
