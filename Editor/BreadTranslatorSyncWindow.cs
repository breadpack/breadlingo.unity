using System.IO;
using System;
using System.Collections.Generic;
using System.Xml.Linq;
using System.Linq;
using UnityEditor.Localization;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;
using BreadTranslator.Unity;
using UnityEditor;
using UnityEngine;

namespace BreadTranslator.Unity.Editor
{
    public sealed class BreadTranslatorSyncWindow : EditorWindow
    {
        private Vector2 scroll;
        private StringTableScanReport lastScan;
        private XliffApplyReport lastApply;
        private string status = "Ready";
        private bool syncing;
        private BreadTranslatorApiOperation pushOperation;
        private BreadTranslatorApiOperation pullOperation;
        private string previewXliff;
        private string previewLocale;
        private Dictionary<string, string> previewTargets;

        [MenuItem("Window/BreadLingo/Unity Localization")]
        public static void ShowWindow()
        {
            GetWindow<BreadTranslatorSyncWindow>("BreadLingo Localization");
        }

        private void OnGUI()
        {
            var settings = BreadTranslatorSettingsProvider.LoadOrCreateSettings();
            EditorGUILayout.LabelField("BreadLingo Unity Localization", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Scans Unity Localization String Tables using stable collection GUID and entry ID identity. Exported XLIFF can be translated remotely and safely applied to an existing target locale table.", MessageType.Info);

            EditorGUI.BeginChangeCheck();
            settings.SourceLocaleCode = EditorGUILayout.TextField("Source Locale", settings.SourceLocaleCode);
            settings.TargetLocaleCode = EditorGUILayout.TextField("Target Locale", settings.TargetLocaleCode);
            settings.ExportDirectory = EditorGUILayout.TextField("Export Directory", settings.ExportDirectory);
            if (EditorGUI.EndChangeCheck())
            {
                lastScan = null;
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Scan String Tables")) Scan(settings);
                using (new EditorGUI.DisabledScope(lastScan == null || lastScan.EntryCount == 0))
                {
                    if (GUILayout.Button("Export XLIFF 1.2")) Export(settings, false);
                    if (GUILayout.Button("Export XLIFF 2.0")) Export(settings, true);
                    if (GUILayout.Button("Export CSV")) ExportCsv(settings);
                }
                if (GUILayout.Button("Apply Pulled XLIFF")) Apply(settings);
                if (GUILayout.Button("Apply CSV")) ApplyCsv(settings);
            }
            using (new EditorGUI.DisabledScope(syncing))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Push Source XLIFF")) Push(settings);
                if (GUILayout.Button("Pull Approved for Preview")) PullAndApply(settings);
            }

            if (previewXliff != null)
            {
                EditorGUILayout.LabelField("Translation preview · " + previewLocale, EditorStyles.boldLabel);
                var parsed = XliffAdapter.Parse(previewXliff);
                var shown = 0;
                foreach (var unit in parsed.Units)
                {
                    if (string.IsNullOrEmpty(unit.TargetText)) continue;
                    if (++shown > 100) { EditorGUILayout.LabelField("Showing first 100 translations. Export the artifact to inspect the full set."); break; }
                    EditorGUILayout.LabelField(unit.Key + " · " + unit.StableId);
                    previewTargets.TryGetValue(unit.StableId, out var local);
                    EditorGUILayout.LabelField("Local", local ?? "");
                    EditorGUILayout.LabelField("Approved", unit.TargetText);
                }
                using (new EditorGUI.DisabledScope(lastApply == null || lastApply.HasConflicts || lastApply.Applied == 0 || syncing))
                {
                    if (GUILayout.Button("Apply Reviewed Translations"))
                    {
                        lastApply = XliffAdapter.Apply(previewXliff, previewLocale, false, previewTargets);
                        status = lastApply.HasConflicts ? "Apply blocked: refresh and review conflicts." : $"Saved {lastApply.Applied} translations. Undo is available.";
                        if (!lastApply.HasConflicts) previewXliff = null;
                    }
                }
                if (GUILayout.Button("Discard Preview")) previewXliff = null;
            }

            EditorGUILayout.LabelField("Status", status);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (lastScan == null)
            {
                EditorGUILayout.LabelField("No scan report yet.");
            }
            else
            {
                EditorGUILayout.LabelField("Source Locale", lastScan.SourceLocale);
                EditorGUILayout.LabelField("Tables", lastScan.TableCount.ToString());
                EditorGUILayout.LabelField("Entries", lastScan.EntryCount.ToString());
                foreach (var warning in lastScan.Warnings) EditorGUILayout.HelpBox(warning, MessageType.Warning);
                foreach (var record in lastScan.Records)
                {
                    EditorGUILayout.LabelField(record.StableId, $"{record.Key} · {record.SourceLocale} · {record.SourceText}");
                }
            }

            if (lastApply != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Last Apply", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Applied", lastApply.Applied.ToString());
                EditorGUILayout.LabelField("Unchanged", lastApply.Unchanged.ToString());
                EditorGUILayout.LabelField("Conflicts", lastApply.Conflicts.Count.ToString());
                foreach (var conflict in lastApply.Conflicts)
                {
                    EditorGUILayout.HelpBox($"{conflict.Code} · {conflict.StableId} · {conflict.Message}", MessageType.Warning);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void Scan(BreadTranslatorProjectSettings settings)
        {
            lastScan = StringTableScanner.ScanProject(settings.SourceLocaleCode);
            status = $"Scanned {lastScan.EntryCount} entries from {lastScan.TableCount} collections.";
        }

        private void Export(BreadTranslatorProjectSettings settings, bool version20)
        {
            var report = lastScan ?? StringTableScanner.ScanProject(settings.SourceLocaleCode);
            if (report.EntryCount == 0)
            {
                status = "No source entries are available to export.";
                return;
            }
            var defaultDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(settings.ExportDirectory) ? "Localization/BreadTranslator" : settings.ExportDirectory);
            Directory.CreateDirectory(defaultDirectory);
            var extension = version20 ? "2.0.xlf" : "1.2.xlf";
            var outputPath = EditorUtility.SaveFilePanel("Export BreadTranslator XLIFF", defaultDirectory, $"breadtranslator-{settings.TargetLocaleCode}.{extension}", "xlf");
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            var content = version20
                ? XliffAdapter.BuildXliff20(report, settings.TargetLocaleCode)
                : XliffAdapter.BuildXliff12(report, settings.TargetLocaleCode);
            File.WriteAllText(outputPath, content, new System.Text.UTF8Encoding(false));
            status = $"Exported {report.EntryCount} entries to {outputPath}.";
        }

        private void Apply(BreadTranslatorProjectSettings settings)
        {
            var inputPath = EditorUtility.OpenFilePanel("Apply BreadTranslator XLIFF", "", "xlf,xliff");
            if (string.IsNullOrWhiteSpace(inputPath)) return;
            try
            {
                SetPreview(File.ReadAllText(inputPath), settings.TargetLocaleCode, CaptureTargets(settings.TargetLocaleCode));
            }
            catch (System.Exception error)
            {
                lastApply = XliffApplyReport.Blocked("XLIFF could not be parsed or applied.");
                status = $"Apply failed: {error.GetType().Name}.";
            }
        }

        private void ExportCsv(BreadTranslatorProjectSettings settings)
        {
            var report = lastScan ?? StringTableScanner.ScanProject(settings.SourceLocaleCode);
            if (report.EntryCount == 0) { status = "No source entries are available to export."; return; }
            var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(settings.ExportDirectory) ? "Localization/BreadTranslator" : settings.ExportDirectory);
            Directory.CreateDirectory(directory);
            var outputPath = EditorUtility.SaveFilePanel("Export BreadTranslator CSV", directory, $"breadtranslator-{settings.TargetLocaleCode}.csv", "csv");
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            File.WriteAllText(outputPath, CsvAdapter.ExportCsv(report, settings.TargetLocaleCode), new System.Text.UTF8Encoding(false));
            status = $"Exported {report.EntryCount} CSV entries to {outputPath}.";
        }

        private void ApplyCsv(BreadTranslatorProjectSettings settings)
        {
            var inputPath = EditorUtility.OpenFilePanel("Apply BreadTranslator CSV", "", "csv");
            if (string.IsNullOrWhiteSpace(inputPath)) return;
            if (!EditorUtility.DisplayDialog("Apply CSV translations", "This replaces matching target cells with nonempty CSV translations after validation. Commit or back up your tables first. Undo is available.", "Apply", "Cancel")) return;
            var report = CsvAdapter.ApplyCsv(File.ReadAllText(inputPath), settings.TargetLocaleCode);
            status = report.HasConflicts ? $"CSV apply blocked by {report.Conflicts.Count} conflicts." : $"CSV applied {report.Applied}; {report.Unchanged} unchanged.";
        }

        private void Push(BreadTranslatorProjectSettings settings)
        {
            var report = StringTableScanner.ScanProject(settings.SourceLocaleCode);
            if (report.EntryCount == 0)
            {
                status = "No source entries are available to push.";
                return;
            }
            syncing = true;
            status = "Pushing source XLIFF to BreadTranslator...";
            if (pushOperation == null || !pushOperation.IsActive) pushOperation = BreadTranslatorApiOperation.Create("xliff-push");
            var document = XDocument.Parse(XliffAdapter.BuildXliff12(report, settings.TargetLocaleCode));
            var targets = CaptureTargets(settings.TargetLocaleCode);
            foreach (var unit in document.Descendants().Where(e => e.Name.LocalName == "trans-unit"))
            {
                var stableId = (string)unit.Attribute(XName.Get("stable-id", "urn:breadtranslator:unity:1"));
                if (targets.TryGetValue(stableId, out var value)) unit.Elements().First(e => e.Name.LocalName == "target").Value = value;
            }
            BreadTranslatorApiClient.PushXliff(settings, document.ToString(SaveOptions.DisableFormatting), pushOperation, result =>
            {
                syncing = false;
                if (result.Success || result.ErrorCode == "cancelled") pushOperation = null;
                status = result.Success
                    ? $"Push completed with HTTP {result.StatusCode}."
                    : $"Push failed: {result.ErrorCode ?? "unknown_error"} (HTTP {result.StatusCode}).";
                Repaint();
            });
        }

        private void PullAndApply(BreadTranslatorProjectSettings settings)
        {
            syncing = true;
            status = "Pulling approved XLIFF from BreadTranslator...";
            var capturedLocale = settings.TargetLocaleCode;
            var capturedTargets = CaptureTargets(capturedLocale);
            if (pullOperation == null || !pullOperation.IsActive) pullOperation = BreadTranslatorApiOperation.Create("xliff-pull");
            BreadTranslatorApiClient.PullApprovedXliff(settings, pullOperation, result =>
            {
                syncing = false;
                if (!result.Success)
                {
                    if (result.ErrorCode == "cancelled") pullOperation = null;
                    status = $"Pull failed: {result.ErrorCode ?? "unknown_error"} (HTTP {result.StatusCode}).";
                    Repaint();
                    return;
                }
                pullOperation = null;
                try
                {
                    SetPreview(result.Xliff, capturedLocale, capturedTargets);
                }
                catch (System.Exception error)
                {
                    lastApply = XliffApplyReport.Blocked("Pulled XLIFF could not be parsed or applied.");
                    status = $"Pull apply failed: {error.GetType().Name}.";
                }
                Repaint();
            });
        }

        private void SetPreview(string xliff, string locale, Dictionary<string, string> targets)
        {
            if (XliffAdapter.Parse(xliff).Units.Count == 0) { previewXliff = null; lastApply = null; status = "No approved translations are available for " + locale + "."; return; }
            previewXliff = xliff; previewLocale = locale; previewTargets = targets;
            lastApply = XliffAdapter.Apply(xliff, locale, true, targets);
            status = lastApply.HasConflicts ? $"Preview blocked by {lastApply.Conflicts.Count} conflicts." : $"Review {lastApply.Applied} changes before applying.";
        }

        private static Dictionary<string, string> CaptureTargets(string locale)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var collection in LocalizationEditorSettings.GetStringTableCollections())
            {
                var table = collection.GetTable(new LocaleIdentifier(locale)) as StringTable;
                if (table == null) continue;
                foreach (var entry in collection.SharedData.Entries)
                {
                    var target = table.GetEntry(entry.Id);
                    if (target != null) values[$"unity:{collection.SharedData.TableCollectionNameGuid}:{entry.Id}"] = target.Value ?? "";
                }
            }
            return values;
        }

        private void OnDisable() { pushOperation?.Cancel(); pullOperation?.Cancel(); }
    }
}
