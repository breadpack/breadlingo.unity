using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace BreadTranslator.Unity.Editor
{
    [Serializable]
    public sealed class BreadTranslatorBatchReport
    {
        public string schema = "breadtranslator.connector-flow.v1";
        public string command;
        public bool success;
        public bool dryRun;
        public bool noOp;
        public string operationId;
        public string idempotencyKeyFingerprint;
        public int exitCode;
        public string errorCode;
        public long httpStatus;
        public string artifactId;
        public string issueId;
        public BreadTranslatorCaptureContext context;
        public BreadTranslatorBatchStep[] steps = Array.Empty<BreadTranslatorBatchStep>();
        public BreadTranslatorBatchBlocker[] blockers = Array.Empty<BreadTranslatorBatchBlocker>();
        public BreadTranslatorBatchArtifact[] artifacts = Array.Empty<BreadTranslatorBatchArtifact>();

        public static BreadTranslatorBatchReport Failure(string commandName, int code, string error)
        {
            var errorCode = Sanitize(error ?? "unknown_error");
            return new BreadTranslatorBatchReport
            {
                command = commandName,
                success = false,
                noOp = true,
                exitCode = code,
                errorCode = errorCode,
                blockers = new[] { BreadTranslatorBatchBlocker.Create(commandName, errorCode, "The connector operation failed.") }
            };
        }

        public static void Write(string outputPath, BreadTranslatorBatchReport report)
        {
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("A JSON report output path is required.", nameof(outputPath));
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = fullPath + ".tmp";
            var backup = fullPath + ".bak";
            report.operationId = string.IsNullOrWhiteSpace(report.operationId) ? StableId("flow", report.command ?? "unknown") : Sanitize(report.operationId);
            report.idempotencyKeyFingerprint = SafeIdempotencyFingerprint(report.idempotencyKeyFingerprint);
            if (!report.success && (report.blockers == null || report.blockers.Length == 0))
                report.blockers = new[] { BreadTranslatorBatchBlocker.Create(report.command, report.errorCode, "The connector operation failed.") };
            File.WriteAllText(temporary, JsonUtility.ToJson(report, true), new UTF8Encoding(false));
            if (File.Exists(fullPath))
            {
                if (File.Exists(backup)) File.Delete(backup);
                File.Replace(temporary, fullPath, backup);
                File.Delete(backup);
            }
            else File.Move(temporary, fullPath);
        }

        internal static string StableId(string prefix, string value)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                return prefix + "_" + BitConverter.ToString(hash, 0, 12).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        internal static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return System.Text.RegularExpressions.Regex.Replace(value, "(?i)(token|authorization|signature|secret)([=: ]+)[^&\\s]+", "$1$2[redacted]");
        }

        internal static string SafeIdempotencyFingerprint(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                System.Text.RegularExpressions.Regex.IsMatch(value, "^idemfp_[a-f0-9]{24}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                    ? value
                    : null;
        }
    }

    [Serializable] public sealed class BreadTranslatorBatchStep { public string name; public string status; public bool changed; }
    [Serializable] public sealed class BreadTranslatorBatchArtifact { public string id; public string kind; public string path; public string checksum; }
    [Serializable]
    public sealed class BreadTranslatorBatchBlocker
    {
        public string id; public string step; public string code; public string message;
        public static BreadTranslatorBatchBlocker Create(string stepName, string errorCode, string text) => new BreadTranslatorBatchBlocker
        {
            id = BreadTranslatorBatchReport.StableId("blocker", (stepName ?? "unknown") + "\0" + (errorCode ?? "unknown_error")),
            step = stepName,
            code = errorCode,
            message = BreadTranslatorBatchReport.Sanitize(text)
        };
    }
}
