using System;
using System.Collections.Generic;
using System.Linq;
using BreadTranslator.Unity;
using UnityEditor;
using UnityEngine;

namespace BreadTranslator.Unity.Editor
{
    public sealed class BreadTranslatorIssueReporterWindow : EditorWindow
    {
        private readonly HashSet<string> selectedStableIds = new HashSet<string>(StringComparer.Ordinal);
        private BreadTranslatorLqaIssueDraft issue = new BreadTranslatorLqaIssueDraft();
        private StringTableScanReport scan;
        private Texture2D screenshot;
        private BreadTranslatorCaptureContext context;
        private BreadTranslatorApiOperation operation;
        private Vector2 scroll;
        private string status = "Capture the Game View or the selected UI element.";
        private MessageType statusType = MessageType.Info;
        private bool busy;

        [MenuItem("Window/BreadTranslator/Report LQA Issue")]
        public static void ShowWindow()
        {
            GetWindow<BreadTranslatorIssueReporterWindow>("BreadTranslator LQA");
        }

        private void OnDisable()
        {
            operation?.Cancel();
            if (screenshot != null) DestroyImmediate(screenshot);
        }

        private void OnGUI()
        {
            var settings = BreadTranslatorSettingsProvider.LoadOrCreateSettings();
            EditorGUILayout.LabelField("Unity LQA Issue Reporter", EditorStyles.boldLabel);
            var selected = Selection.activeGameObject;
            EditorGUILayout.LabelField("Selection", selected == null ? "None" : selected.name);
            EditorGUILayout.LabelField("Connection", Application.internetReachability == NetworkReachability.NotReachable ? "Offline" : "Available");

            using (new EditorGUI.DisabledScope(busy))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Capture Game View")) Capture(settings, false);
                using (new EditorGUI.DisabledScope(selected == null || selected.GetComponent<RectTransform>() == null))
                {
                    if (GUILayout.Button("Capture Selected UI")) Capture(settings, true);
                }
                if (GUILayout.Button("Refresh String Tables")) scan = StringTableScanner.ScanProject(settings.SourceLocaleCode);
            }

            if (screenshot != null)
            {
                var aspect = screenshot.height == 0 ? 1f : (float)screenshot.width / screenshot.height;
                var preview = GUILayoutUtility.GetAspectRect(aspect, GUILayout.MaxHeight(220));
                EditorGUI.DrawPreviewTexture(preview, screenshot, null, ScaleMode.ScaleToFit);
                EditorGUILayout.LabelField("Capture", $"{screenshot.width} x {screenshot.height} PNG");
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawIssueFields();
            DrawStringReferences(settings);
            DrawContext();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.HelpBox(status, statusType);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(busy || screenshot == null || context == null || string.IsNullOrWhiteSpace(issue.title)))
                {
                    if (GUILayout.Button("Upload & Create Issue")) Submit(settings);
                }
                using (new EditorGUI.DisabledScope(!busy))
                {
                    if (GUILayout.Button("Cancel"))
                    {
                        operation?.Cancel();
                        status = "Cancellation requested.";
                        statusType = MessageType.Warning;
                    }
                }
            }
        }

        private void DrawIssueFields()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Issue", EditorStyles.boldLabel);
            issue.title = EditorGUILayout.TextField("Title", issue.title);
            issue.category = EditorGUILayout.TextField("Category", issue.category);
            var severities = new[] { "minor", "major", "critical" };
            var severityIndex = Math.Max(0, Array.IndexOf(severities, issue.severity));
            issue.severity = severities[EditorGUILayout.Popup("Severity", severityIndex, severities)];
            EditorGUILayout.LabelField("Description");
            issue.description = EditorGUILayout.TextArea(issue.description, GUILayout.MinHeight(64));
        }

        private void DrawStringReferences(BreadTranslatorProjectSettings settings)
        {
            if (scan == null) scan = StringTableScanner.ScanProject(settings.SourceLocaleCode);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Candidate String References", EditorStyles.boldLabel);
            if (scan.EntryCount == 0)
            {
                EditorGUILayout.HelpBox("No source String Table entries were found.", MessageType.Warning);
                return;
            }
            foreach (var record in scan.Records.Take(200))
            {
                var selected = selectedStableIds.Contains(record.StableId);
                var next = EditorGUILayout.ToggleLeft($"{record.TableCollectionName} / {record.Key} ({record.EntryId})", selected);
                if (next) selectedStableIds.Add(record.StableId); else selectedStableIds.Remove(record.StableId);
            }
            if (scan.EntryCount > 200) EditorGUILayout.HelpBox("Only the first 200 entries are shown. Refine the source tables before linking strings.", MessageType.Info);
        }

        private void DrawContext()
        {
            if (context == null) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Allowed Context", EditorStyles.boldLabel);
            foreach (var field in context.metadata) EditorGUILayout.LabelField(field.key, field.value);
        }

        private void Capture(BreadTranslatorProjectSettings settings, bool selectedUi)
        {
            try
            {
                if (screenshot != null) DestroyImmediate(screenshot);
                var selected = Selection.activeGameObject;
                screenshot = selectedUi
                    ? BreadTranslatorContextCapture.CaptureSelectedUi(selected.GetComponent<RectTransform>())
                    : BreadTranslatorContextCapture.CaptureGameView();
                RefreshContext(settings, selected);
                status = selectedUi ? "Selected UI capture is ready." : "Game View capture is ready.";
                statusType = MessageType.Info;
            }
            catch (Exception error)
            {
                status = $"Capture failed: {error.GetType().Name}.";
                statusType = MessageType.Error;
            }
        }

        private void RefreshContext(BreadTranslatorProjectSettings settings, GameObject selected)
        {
            var records = (scan?.Records ?? Array.Empty<StringTableRecord>()).Where(record => selectedStableIds.Contains(record.StableId));
            context = BreadTranslatorContextCapture.Collect(settings, selected, records);
            if (screenshot != null)
            {
                context.width = screenshot.width;
                context.height = screenshot.height;
                var resolution = context.metadata.FirstOrDefault(field => field.key == "resolution");
                if (resolution != null) resolution.value = $"{screenshot.width}x{screenshot.height}";
            }
        }

        private void Submit(BreadTranslatorProjectSettings settings)
        {
            if (Application.internetReachability == NetworkReachability.NotReachable)
            {
                status = "Offline. The screenshot remains in memory; reconnect and retry.";
                statusType = MessageType.Error;
                return;
            }
            RefreshContext(settings, Selection.activeGameObject);
            byte[] png;
            try { png = screenshot.EncodeToPNG(); }
            catch (Exception error)
            {
                status = $"PNG encoding failed: {error.GetType().Name}.";
                statusType = MessageType.Error;
                return;
            }
            busy = true;
            status = "Uploading private screenshot and creating LQA issue...";
            statusType = MessageType.Info;
            if (operation == null || !operation.IsActive) operation = BreadTranslatorApiOperation.Create("lqa-report");
            BreadTranslatorApiClient.ReportLqaIssue(settings, png, context, issue, operation, result =>
            {
                busy = false;
                if (result.Success || result.ErrorCode == "cancelled") operation = null;
                status = result.Success
                    ? $"Issue {result.IssueId ?? "created"} created with private artifact {result.ArtifactId}."
                    : result.ErrorCode == "cancelled"
                        ? "Upload cancelled. No token or signed URL was retained."
                        : $"Issue report failed: {result.ErrorCode} (HTTP {result.StatusCode}).";
                statusType = result.Success ? MessageType.Info : result.ErrorCode == "cancelled" ? MessageType.Warning : MessageType.Error;
                Repaint();
            });
        }
    }
}
