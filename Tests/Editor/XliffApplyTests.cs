using System;
using System.Collections.Generic;
using System.Security;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

namespace BreadTranslator.Unity.Editor.Tests
{
    public sealed class XliffApplyTests
    {
        private const string SourceLocale = "en";
        private const string TargetLocale = "ja";

        private string assetDirectory;
        private StringTableCollection collection;
        private StringTable sourceTable;
        private StringTable targetTable;

        [SetUp]
        public void SetUp()
        {
            assetDirectory = $"Assets/__BreadTranslatorXliffApplyTests_{Guid.NewGuid():N}";
            collection = LocalizationEditorSettings.CreateStringTableCollection(
                $"BreadTranslator XLIFF Apply {Guid.NewGuid():N}",
                assetDirectory,
                new List<UnityEngine.Localization.Locale>());
            sourceTable = (StringTable)collection.AddNewTable(new LocaleIdentifier(SourceLocale));
            targetTable = (StringTable)collection.AddNewTable(new LocaleIdentifier(TargetLocale));
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            AssetDatabase.DeleteAsset(assetDirectory);
            AssetDatabase.SaveAssets();
        }

        [Test]
        public void PreviewDoesNotWriteAndChangedLocalTargetBlocksApply()
        {
            var id = AddEntry("ui.preview", "Preview", "Local");
            var artifact = BuildXliff(Unit(id, "ui.preview", "Preview", "プレビュー"));
            var targets = new Dictionary<string, string> { [$"unity:{collection.SharedData.TableCollectionNameGuid}:{id}"] = "Local" };
            var preview = XliffAdapter.Apply(artifact, TargetLocale, true, targets);
            Assert.That(preview.Applied, Is.EqualTo(1));
            Assert.That(targetTable.GetEntry(id).Value, Is.EqualTo("Local"));
            targetTable.GetEntry(id).Value = "Edited while reviewing";
            var apply = XliffAdapter.Apply(artifact, TargetLocale, false, targets);
            Assert.That(apply.Applied, Is.Zero);
            Assert.That(apply.Conflicts[0].Code, Is.EqualTo("local_target_changed"));
            Assert.That(targetTable.GetEntry(id).Value, Is.EqualTo("Edited while reviewing"));
        }

        [Test]
        public void BlankTemplateTargetNeverClearsLocalTranslation()
        {
            var id = AddEntry("ui.blank", "Text", "Preserve me");
            var apply = XliffAdapter.Apply(BuildXliff(Unit(id, "ui.blank", "Text", "")));
            Assert.That(apply.Applied, Is.Zero);
            Assert.That(targetTable.GetEntry(id).Value, Is.EqualTo("Preserve me"));
        }

        [Test]
        public void ApplyMutatesOnlyTargetTable()
        {
            var entryId = AddEntry("ui.greeting", "Hello {name}", "Old greeting", true);

            var report = XliffAdapter.Apply(BuildXliff(Unit(entryId, "ui.greeting", "Hello {name}", "こんにちは {name}")));

            Assert.That(report.HasConflicts, Is.False);
            Assert.That(report.Applied, Is.EqualTo(1));
            Assert.That(report.Unchanged, Is.Zero);
            Assert.That(sourceTable.GetEntry(entryId).Value, Is.EqualTo("Hello {name}"));
            Assert.That(sourceTable.GetEntry(entryId).IsSmart, Is.True);
            Assert.That(targetTable.GetEntry(entryId).Value, Is.EqualTo("こんにちは {name}"));
            Assert.That(targetTable.GetEntry(entryId).IsSmart, Is.True);
        }

        [Test]
        public void ValidationConflictIsAllOrNothing()
        {
            var validEntryId = AddEntry("ui.play", "Play", "Old play");
            var conflictingEntryId = AddEntry("ui.quit", "Quit", "Old quit");
            var artifact = BuildXliff(
                Unit(validEntryId, "ui.play", "Play", "プレイ"),
                Unit(conflictingEntryId, "ui.quit", "Stale source", "終了"));

            var report = XliffAdapter.Apply(artifact);

            Assert.That(report.HasConflicts, Is.True);
            Assert.That(report.Applied, Is.Zero);
            Assert.That(report.Conflicts, Has.Count.EqualTo(1));
            Assert.That(report.Conflicts[0].Code, Is.EqualTo("source_changed"));
            Assert.That(targetTable.GetEntry(validEntryId).Value, Is.EqualTo("Old play"));
            Assert.That(targetTable.GetEntry(conflictingEntryId).Value, Is.EqualTo("Old quit"));
        }

        [Test]
        public void ApplyMutationCanBeRestoredWithUndo()
        {
            var entryId = AddEntry("ui.continue", "Continue", "Old continue");
            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();

            var report = XliffAdapter.Apply(BuildXliff(Unit(entryId, "ui.continue", "Continue", "続ける")));
            Undo.CollapseUndoOperations(undoGroup);

            Assert.That(report.Applied, Is.EqualTo(1));
            Assert.That(targetTable.GetEntry(entryId).Value, Is.EqualTo("続ける"));
            Undo.PerformUndo();
            Assert.That(targetTable.GetEntry(entryId).Value, Is.EqualTo("Old continue"));
            Assert.That(sourceTable.GetEntry(entryId).Value, Is.EqualTo("Continue"));
        }

        [Test]
        public void ApplyingSameArtifactTwiceIsNoOp()
        {
            var entryId = AddEntry("ui.settings", "Settings", "Old settings");
            var artifact = BuildXliff(Unit(entryId, "ui.settings", "Settings", "設定"));

            var first = XliffAdapter.Apply(artifact);
            var second = XliffAdapter.Apply(artifact);

            Assert.That(first.Applied, Is.EqualTo(1));
            Assert.That(second.HasConflicts, Is.False);
            Assert.That(second.Applied, Is.Zero);
            Assert.That(second.Unchanged, Is.EqualTo(1));
            Assert.That(targetTable.GetEntry(entryId).Value, Is.EqualTo("設定"));
            Assert.That(sourceTable.GetEntry(entryId).Value, Is.EqualTo("Settings"));
        }

        private long AddEntry(string key, string source, string target, bool isSmart = false)
        {
            var sourceEntry = sourceTable.AddEntry(key, source);
            sourceEntry.IsSmart = isSmart;
            var targetEntry = targetTable.AddEntry(sourceEntry.KeyId, target);
            targetEntry.IsSmart = false;
            EditorUtility.SetDirty(sourceTable);
            EditorUtility.SetDirty(targetTable);
            EditorUtility.SetDirty(collection.SharedData);
            AssetDatabase.SaveAssets();
            return sourceEntry.KeyId;
        }

        private string Unit(long entryId, string key, string source, string target)
        {
            var guid = collection.SharedData.TableCollectionNameGuid.ToString();
            return $"<trans-unit id=\"{entryId}\" resname=\"{Escape(key)}\" bt:stable-id=\"unity:{guid}:{entryId}\" bt:table-guid=\"{guid}\"><source>{Escape(source)}</source><target>{Escape(target)}</target></trans-unit>";
        }

        private static string BuildXliff(params string[] units)
        {
            return $"<xliff xmlns=\"urn:oasis:names:tc:xliff:document:1.2\" xmlns:bt=\"urn:breadtranslator:unity:1\" version=\"1.2\"><file source-language=\"{SourceLocale}\" target-language=\"{TargetLocale}\"><body>{string.Concat(units)}</body></file></xliff>";
        }

        private static string Escape(string value)
        {
            return SecurityElement.Escape(value) ?? "";
        }
    }
}
