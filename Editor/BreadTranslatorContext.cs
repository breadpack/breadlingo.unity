using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BreadTranslator.Unity.Editor
{
    [Serializable]
    public sealed class BreadTranslatorStringReference
    {
        public string tableCollectionGuid;
        public string entryId;
        public string key;

        public string StableId => $"unity:{tableCollectionGuid ?? ""}:{entryId ?? ""}";
    }

    [Serializable]
    public sealed class BreadTranslatorAssetReference
    {
        public string tableCollectionGuid;
        public string entryId;
        public string key;

        public string StableId => $"unity-asset:{tableCollectionGuid ?? ""}:{entryId ?? ""}";
    }

    [Serializable]
    public sealed class BreadTranslatorContextField
    {
        public string key;
        public string value;
    }

    [Serializable]
    public sealed class BreadTranslatorCaptureContext
    {
        public string engine = "unity";
        public string branch;
        public string locale;
        public string buildVersion;
        public string buildTarget;
        public string unityVersion;
        public string scene;
        public string prefabPath;
        public string hierarchyPath;
        public int width;
        public int height;
        public BreadTranslatorStringReference[] candidateStringRefs = Array.Empty<BreadTranslatorStringReference>();
        public BreadTranslatorAssetReference[] candidateAssetRefs = Array.Empty<BreadTranslatorAssetReference>();
        public BreadTranslatorContextField[] metadata = Array.Empty<BreadTranslatorContextField>();
    }

    public static class BreadTranslatorMetadataPolicy
    {
        public static ISet<string> ParseAllowlist(string value)
        {
            return new HashSet<string>((value ?? "")
                .Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(IsValidKey), StringComparer.Ordinal);
        }

        public static BreadTranslatorContextField[] Filter(
            IEnumerable<BreadTranslatorContextField> fields,
            ISet<string> allowlist)
        {
            if (fields == null || allowlist == null || allowlist.Count == 0)
            {
                return Array.Empty<BreadTranslatorContextField>();
            }

            return fields
                .Where(field => field != null && IsValidKey(field.key) && allowlist.Contains(field.key))
                .GroupBy(field => field.key, StringComparer.Ordinal)
                .Select(group => new BreadTranslatorContextField
                {
                    key = group.Key,
                    value = SanitizeValue(group.Last().value)
                })
                .OrderBy(field => field.key, StringComparer.Ordinal)
                .ToArray();
        }

        public static bool IsValidKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 64) return false;
            return key.All(character => char.IsLetterOrDigit(character) || character == '_' || character == '-' || character == '.');
        }

        private static string SanitizeValue(string value)
        {
            var result = value ?? "";
            if (result.Length > 2048) result = result.Substring(0, 2048);
            return result.Replace("\0", "");
        }
    }
}
