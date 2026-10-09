using System;
using System.Collections.Generic;
using System.Linq;
using BreadTranslator.Unity;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Components;
using UnityEngine.SceneManagement;

namespace BreadTranslator.Unity.Editor
{
    public static class BreadTranslatorContextCapture
    {
        public static BreadTranslatorCaptureContext Collect(
            BreadTranslatorProjectSettings settings,
            GameObject selectedObject,
            IEnumerable<StringTableRecord> stringRecords = null,
            IEnumerable<AssetTableRecord> assetRecords = null)
        {
            var scene = selectedObject != null && selectedObject.scene.IsValid()
                ? selectedObject.scene
                : SceneManager.GetActiveScene();
            var hierarchyPath = selectedObject == null ? "" : AnimationUtility.CalculateTransformPath(selectedObject.transform, null);
            var prefabPath = selectedObject == null ? "" : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(selectedObject);
            var locale = ResolveLocale(settings);
            var gameViewSize = ResolveGameViewSize();
            var width = gameViewSize.x;
            var height = gameViewSize.y;
            var scannedReferences = (stringRecords ?? Array.Empty<StringTableRecord>())
                .Where(record => record != null)
                .Select(record => new BreadTranslatorStringReference
                {
                    tableCollectionGuid = record.TableCollectionGuid,
                    entryId = record.EntryId,
                    key = record.Key
                })
                .ToArray();
            var references = scannedReferences
                .Concat(CollectSelectionStringReferences(selectedObject))
                .Where(item => !string.IsNullOrWhiteSpace(item.tableCollectionGuid) && !string.IsNullOrWhiteSpace(item.entryId))
                .GroupBy(item => item.StableId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.StableId, StringComparer.Ordinal)
                .ToArray();
            var assetReferences = (assetRecords ?? Array.Empty<AssetTableRecord>())
                .Where(record => record != null && !string.IsNullOrWhiteSpace(record.TableCollectionGuid) && !string.IsNullOrWhiteSpace(record.EntryId))
                .Select(record => new BreadTranslatorAssetReference
                {
                    tableCollectionGuid = record.TableCollectionGuid,
                    entryId = record.EntryId,
                    key = record.Key
                })
                .GroupBy(item => item.StableId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.StableId, StringComparer.Ordinal)
                .ToArray();

            var fields = new List<BreadTranslatorContextField>
            {
                Field("scene", string.IsNullOrWhiteSpace(scene.path) ? scene.name : scene.path),
                Field("hierarchyPath", hierarchyPath),
                Field("prefabPath", prefabPath),
                Field("locale", locale),
                Field("buildVersion", PlayerSettings.bundleVersion),
                Field("buildTarget", EditorUserBuildSettings.activeBuildTarget.ToString()),
                Field("unityVersion", Application.unityVersion),
                Field("resolution", $"{width}x{height}"),
                Field("branch", settings == null ? "" : settings.Branch)
            };
            if (references.Length == 1)
            {
                fields.Add(Field("tableCollectionGuid", references[0].tableCollectionGuid));
                fields.Add(Field("entryId", references[0].entryId));
                fields.Add(Field("stringKey", references[0].key));
            }

            var allowlist = BreadTranslatorMetadataPolicy.ParseAllowlist(settings == null ? "" : settings.ContextMetadataAllowlist);
            return new BreadTranslatorCaptureContext
            {
                branch = settings == null ? "" : settings.Branch,
                locale = locale,
                buildVersion = PlayerSettings.bundleVersion,
                buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                unityVersion = Application.unityVersion,
                scene = string.IsNullOrWhiteSpace(scene.path) ? scene.name : scene.path,
                prefabPath = prefabPath,
                hierarchyPath = hierarchyPath,
                width = width,
                height = height,
                candidateStringRefs = references,
                candidateAssetRefs = assetReferences,
                metadata = BreadTranslatorMetadataPolicy.Filter(fields, allowlist)
            };
        }

        private static IEnumerable<BreadTranslatorStringReference> CollectSelectionStringReferences(GameObject selectedObject)
        {
            if (selectedObject == null) yield break;
            foreach (var component in selectedObject.GetComponentsInChildren<LocalizeStringEvent>(true))
            {
                var localized = component.StringReference;
                if (localized == null) continue;
                var collection = LocalizationEditorSettings.GetStringTableCollection(localized.TableReference);
                if (collection == null || collection.SharedData == null) continue;
                var sharedEntry = collection.SharedData.GetEntryFromReference(localized.TableEntryReference);
                if (sharedEntry == null) continue;
                yield return new BreadTranslatorStringReference
                {
                    tableCollectionGuid = collection.SharedData.TableCollectionNameGuid.ToString(),
                    entryId = sharedEntry.Id.ToString(),
                    key = sharedEntry.Key ?? ""
                };
            }
        }

        public static Texture2D CaptureGameView()
        {
            if (Application.isBatchMode) throw new InvalidOperationException("Game View capture is unavailable in batchmode.");
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            if (texture == null) throw new InvalidOperationException("Unity did not return a Game View screenshot.");
            return texture;
        }

        public static Texture2D CaptureSelectedUi(RectTransform selected, Camera camera = null)
        {
            if (selected == null) throw new ArgumentNullException(nameof(selected));
            var source = CaptureGameView();
            try
            {
                var corners = new Vector3[4];
                selected.GetWorldCorners(corners);
                var canvas = selected.GetComponentInParent<Canvas>();
                var renderCamera = camera ?? (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null);
                var points = corners.Select(point => RectTransformUtility.WorldToScreenPoint(renderCamera, point)).ToArray();
                var xMin = Mathf.Clamp(Mathf.FloorToInt(points.Min(point => point.x)), 0, source.width - 1);
                var xMax = Mathf.Clamp(Mathf.CeilToInt(points.Max(point => point.x)), xMin + 1, source.width);
                var yMin = Mathf.Clamp(Mathf.FloorToInt(points.Min(point => point.y)), 0, source.height - 1);
                var yMax = Mathf.Clamp(Mathf.CeilToInt(points.Max(point => point.y)), yMin + 1, source.height);
                var result = new Texture2D(xMax - xMin, yMax - yMin, TextureFormat.RGBA32, false, false);
                result.SetPixels(source.GetPixels(xMin, yMin, result.width, result.height));
                result.Apply(false, false);
                return result;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        private static string ResolveLocale(BreadTranslatorProjectSettings settings)
        {
            try
            {
                var selected = LocalizationSettings.SelectedLocale;
                if (selected != null) return selected.Identifier.Code;
            }
            catch (Exception)
            {
                // LocalizationSettings can be uninitialized in edit mode or batchmode.
            }
            return settings == null ? "" : settings.TargetLocaleCode;
        }

        private static Vector2Int ResolveGameViewSize()
        {
            try
            {
                var size = Handles.GetMainGameViewSize();
                if (size.x > 0 && size.y > 0) return new Vector2Int(Mathf.RoundToInt(size.x), Mathf.RoundToInt(size.y));
            }
            catch (Exception)
            {
                // A Game View may not exist in a fresh batchmode project.
            }
            return new Vector2Int(Math.Max(1, Screen.width), Math.Max(1, Screen.height));
        }

        private static BreadTranslatorContextField Field(string key, string value)
        {
            return new BreadTranslatorContextField { key = key, value = value ?? "" };
        }
    }
}
