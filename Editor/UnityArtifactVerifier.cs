using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BreadTranslator.Unity.Editor
{
    public static class UnityArtifactVerifier
    {
        public static UnityArtifactVerificationResult VerifyExport(
            string xliff,
            string serverChecksumSha256,
            string expectedSourceLocale,
            string expectedTargetLocale)
        {
            var expectedChecksum = NormalizeChecksum(serverChecksumSha256);
            var actualChecksum = Sha256Hex(xliff ?? "");
            if (!FixedTimeEquals(expectedChecksum, actualChecksum))
            {
                throw new UnityArtifactVerificationException("artifact_checksum_mismatch", "Downloaded artifact does not match its server checksum.");
            }

            ParsedXliff parsed;
            try
            {
                parsed = XliffAdapter.Parse(xliff);
            }
            catch (Exception error) when (error is ArgumentException || error is System.Xml.XmlException || error is FormatException)
            {
                throw new UnityArtifactVerificationException("xliff_malformed", "Downloaded artifact is not a valid Unity XLIFF document.");
            }

            if (parsed.Version != "1.2" && parsed.Version != "2.0")
            {
                throw new UnityArtifactVerificationException("xliff_version_unsupported", "Downloaded artifact must use XLIFF 1.2 or 2.0.");
            }

            RequireLocale(parsed.SourceLocale, expectedSourceLocale, "source");
            RequireLocale(parsed.TargetLocale, expectedTargetLocale, "target");
            // An authenticated export with no approved units is a valid empty result.
            if (parsed.Units.Count > 0) ValidateIdentities(parsed);
            return new UnityArtifactVerificationResult(actualChecksum, parsed.SourceLocale, parsed.TargetLocale, parsed.Units.Count);
        }

        public static void ValidateIdentities(ParsedXliff parsed)
        {
            if (parsed == null) throw new ArgumentNullException(nameof(parsed));
            if (parsed.Units == null || parsed.Units.Count == 0)
            {
                throw new UnityArtifactVerificationException("xliff_units_missing", "Unity XLIFF contains no translation units.");
            }

            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var unit in parsed.Units)
            {
                if (unit == null || string.IsNullOrWhiteSpace(unit.TableCollectionGuid) ||
                    string.IsNullOrWhiteSpace(unit.EntryId) || string.IsNullOrWhiteSpace(unit.Key))
                {
                    throw new UnityArtifactVerificationException("xliff_identity_missing", "Every Unity XLIFF unit requires table GUID, entry ID, and key identity.");
                }
                if (!long.TryParse(unit.EntryId, out _))
                {
                    throw new UnityArtifactVerificationException("xliff_entry_id_invalid", "Every Unity XLIFF entry ID must be an Int64 value.");
                }
                var expectedStableId = $"unity:{unit.TableCollectionGuid}:{unit.EntryId}";
                if (!string.Equals(unit.StableId, expectedStableId, StringComparison.Ordinal))
                {
                    throw new UnityArtifactVerificationException("xliff_stable_id_mismatch", "Unity XLIFF stable identity does not match its table GUID and entry ID.");
                }
                var identity = unit.TableCollectionGuid + "\0" + unit.EntryId + "\0" + unit.Key;
                if (!identities.Add(identity))
                {
                    throw new UnityArtifactVerificationException("xliff_identity_collision", "Unity XLIFF contains a duplicate table GUID, entry ID, and key identity.");
                }
            }
        }

        public static string Sha256Hex(string content)
        {
            using (var sha256 = SHA256.Create())
            {
                return string.Concat(sha256.ComputeHash(Encoding.UTF8.GetBytes(content ?? "")).Select(value => value.ToString("x2")));
            }
        }

        private static string NormalizeChecksum(string value)
        {
            var normalized = (value ?? "").Trim().ToLowerInvariant();
            if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            {
                var code = string.IsNullOrWhiteSpace(value) ? "artifact_checksum_missing" : "artifact_checksum_invalid";
                throw new UnityArtifactVerificationException(code, "Export response must include a valid server SHA-256 checksum.");
            }
            return normalized;
        }

        private static void RequireLocale(string actual, string expected, string kind)
        {
            if (string.IsNullOrWhiteSpace(expected) || !string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new UnityArtifactVerificationException("artifact_locale_mismatch", $"Unity XLIFF {kind} locale does not match the requested locale.");
            }
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            var difference = left.Length ^ right.Length;
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }
    }

    public sealed class UnityArtifactVerificationResult
    {
        public UnityArtifactVerificationResult(string checksumSha256, string sourceLocale, string targetLocale, int unitCount)
        {
            ChecksumSha256 = checksumSha256;
            SourceLocale = sourceLocale;
            TargetLocale = targetLocale;
            UnitCount = unitCount;
        }
        public string ChecksumSha256 { get; }
        public string SourceLocale { get; }
        public string TargetLocale { get; }
        public int UnitCount { get; }
    }

    public sealed class UnityArtifactVerificationException : Exception
    {
        public UnityArtifactVerificationException(string code, string message) : base(message) { Code = code; }
        public string Code { get; }
    }
}
