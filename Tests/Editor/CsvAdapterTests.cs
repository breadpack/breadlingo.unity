using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace BreadTranslator.Unity.Editor.Tests
{
    public sealed class CsvAdapterTests
    {
        [Test]
        public void ExportAndParsePreserveStableIdentityAndQuotedText()
        {
            var record = new StringTableRecord("guid-ui", "UI", "Assets/UI.asset", "1001", "ui.play", "en", "Play, \"{player}\"", false, Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, string>());
            var csv = CsvAdapter.ExportCsv(new StringTableScanReport(1, 1, "en", new[] { record }, Array.Empty<string>()), "ja");
            var parsed = CsvAdapter.Parse(csv);
            Assert.That(parsed.Rows.Count, Is.EqualTo(1));
            Assert.That(parsed.Rows[0].StableId, Is.EqualTo("unity:guid-ui:1001"));
            Assert.That(parsed.Rows[0].Source, Is.EqualTo("Play, \"{player}\""));
        }

        [Test]
        public void ParseRejectsDuplicateOrMismatchedIdentity()
        {
            const string header = "stable_id,table_guid,entry_id,key,source_locale,target_locale,source,target\n";
            const string row = "unity:guid-ui:1001,guid-ui,1001,ui.play,en,ja,Play,Play\n";
            Assert.Throws<FormatException>(() => CsvAdapter.Parse(header + row + row));
            Assert.Throws<FormatException>(() => CsvAdapter.Parse(header + "unity:wrong:1001,guid-ui,1001,ui.play,en,ja,Play,Play\n"));
        }

        [Test]
        public void BatchCsvCommandContract()
        {
            Assert.That(BreadTranslatorBatchCommands.ApplyConflictExitCode, Is.EqualTo(5));
            Assert.That(typeof(BreadTranslatorBatchCommands).GetMethod("ExportCsv"), Is.Not.Null);
            Assert.That(typeof(BreadTranslatorBatchCommands).GetMethod("ApplyCsv"), Is.Not.Null);
        }
    }
}
