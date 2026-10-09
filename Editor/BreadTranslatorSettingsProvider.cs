using System;
using BreadTranslator.Unity;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace BreadTranslator.Unity.Editor
{
    public static class BreadTranslatorSettingsProvider
    {
        private static string sessionToken = "";
        private static string sessionLqaToken = "";
        private static string tokenDraft;
        private static string lqaTokenDraft;
        private const string TokenPrefsKey = "BreadTranslator.Unity.ConnectorToken";
        private const string LqaTokenPrefsKey = "BreadTranslator.Unity.LqaApiToken";

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Project/BreadLingo", SettingsScope.Project)
            {
                label = "BreadLingo",
                guiHandler = _ => DrawSettings()
            };
        }

        public static string GetToken()
        {
            var environmentToken = Environment.GetEnvironmentVariable("BREADTRANSLATOR_TOKEN");
            if (!string.IsNullOrWhiteSpace(environmentToken))
            {
                return environmentToken.Trim();
            }
            if (Application.isBatchMode) return "";
            return sessionToken;
        }

        public static string GetLqaToken()
        {
            var environmentToken = Environment.GetEnvironmentVariable("BREADTRANSLATOR_LQA_TOKEN");
            if (!string.IsNullOrWhiteSpace(environmentToken)) return environmentToken.Trim();
            if (Application.isBatchMode) return "";
            return sessionLqaToken;
        }

        public static void SetLqaToken(string token)
        {
            sessionLqaToken = (token ?? "").Trim();
            EditorPrefs.DeleteKey(LqaTokenPrefsKey);
        }

        public static void SetToken(string token)
        {
            sessionToken = (token ?? "").Trim();
            EditorPrefs.DeleteKey(TokenPrefsKey);
        }

        private static void DrawSettings()
        {
            var settings = LoadOrCreateSettings();

            EditorGUI.BeginChangeCheck();
            settings.ApiBaseUrl = EditorGUILayout.TextField("API Base URL", settings.ApiBaseUrl);
            settings.WorkspaceSlug = EditorGUILayout.TextField("Workspace Slug", settings.WorkspaceSlug);
            settings.ProjectSlug = EditorGUILayout.TextField("Project Slug", settings.ProjectSlug);
            settings.Branch = EditorGUILayout.TextField("Branch", settings.Branch);
            settings.DefaultSchema = EditorGUILayout.TextField("Default Schema", settings.DefaultSchema);
            settings.SourceLocaleCode = EditorGUILayout.TextField("Source Locale", settings.SourceLocaleCode);
            settings.TargetLocaleCode = EditorGUILayout.TextField("Target Locale", settings.TargetLocaleCode);
            settings.ExportDirectory = EditorGUILayout.TextField("Export Directory", settings.ExportDirectory);
            EditorGUILayout.LabelField("Context Metadata Allowlist");
            settings.ContextMetadataAllowlist = EditorGUILayout.TextArea(settings.ContextMetadataAllowlist, GUILayout.MinHeight(48));
            settings.LocaleMappings = EditorGUILayout.TextArea(settings.LocaleMappings, GUILayout.MinHeight(48));

            tokenDraft = EditorGUILayout.PasswordField("Connector Token", tokenDraft ?? GetToken());
            if (GUILayout.Button("Use Token For This Session"))
            {
                SetToken(tokenDraft);
            }

            lqaTokenDraft = EditorGUILayout.PasswordField("LQA API Token", lqaTokenDraft ?? GetLqaToken());
            if (GUILayout.Button("Use LQA Token For This Session"))
            {
                SetLqaToken(lqaTokenDraft);
            }

            if (GUILayout.Button("Test Token"))
            {
                TestToken(settings);
            }

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }
        }

        public static BreadTranslatorProjectSettings LoadOrCreateSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<BreadTranslatorProjectSettings>(BreadTranslatorProjectSettings.DefaultAssetPath);
            if (settings != null)
            {
                return settings;
            }

            const string editorDirectory = "Assets/Editor";
            if (!AssetDatabase.IsValidFolder(editorDirectory))
            {
                AssetDatabase.CreateFolder("Assets", "Editor");
            }

            settings = ScriptableObject.CreateInstance<BreadTranslatorProjectSettings>();
            AssetDatabase.CreateAsset(settings, BreadTranslatorProjectSettings.DefaultAssetPath);
            AssetDatabase.SaveAssets();
            return settings;
        }

        private static void TestToken(BreadTranslatorProjectSettings settings)
        {
            var token = GetToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                Debug.LogWarning("BreadTranslator connector token is empty.");
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.WorkspaceSlug) || string.IsNullOrWhiteSpace(settings.ProjectSlug))
            {
                Debug.LogWarning("BreadTranslator workspace and project slugs are required before testing a token.");
                return;
            }

            var endpoint = BreadLingoEndpoint.Build(settings, $"/api/workspaces/{Uri.EscapeDataString(settings.WorkspaceSlug)}/projects/{Uri.EscapeDataString(settings.ProjectSlug)}/connectors/jobs");
            var request = UnityWebRequest.Get(endpoint);
            request.redirectLimit = 0;
            request.timeout = 30;
            request.SetRequestHeader("X-Connector-Token", token);
            var operation = request.SendWebRequest();

            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (!operation.isDone)
                {
                    return;
                }

                EditorApplication.update -= tick;
                if (request.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"BreadTranslator token test succeeded for {settings.WorkspaceSlug}/{settings.ProjectSlug}.");
                }
                else
                {
                    Debug.LogWarning($"BreadTranslator token test failed: {request.responseCode} {request.error}");
                }

                request.Dispose();
            };

            EditorApplication.update += tick;
        }

            }
}
