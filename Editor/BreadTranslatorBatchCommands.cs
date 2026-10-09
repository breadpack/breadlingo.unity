using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BreadTranslator.Unity;
using UnityEditor;
using UnityEngine;

namespace BreadTranslator.Unity.Editor
{
    public static class BreadTranslatorBatchCommands
    {
        internal const int InvalidInputExitCode = 4;
        internal const int ApplyConflictExitCode = 5;
        internal const int HttpFailureExitCode = 7;
        internal const int TimeoutExitCode = 8;
        internal const int RemoteConflictExitCode = 9;

        public static void Push()
        {
            const string commandName = "push";
            var output = ReportOutput();
            BreadTranslatorApiOperation operation = null;
            try
            {
                var settings = BreadTranslatorSettingsProvider.LoadOrCreateSettings();
                if (!HasEnvironmentToken())
                {
                    FinishBatchReport(output, BreadTranslatorBatchReport.Failure(commandName, InvalidInputExitCode, "connector_token_environment_missing"));
                    return;
                }
                var sourceLocale = ReadArgument("-breadTranslatorSourceLocale") ?? settings.SourceLocaleCode;
                var targetLocale = ReadArgument("-breadTranslatorTargetLocale") ?? settings.TargetLocaleCode;
                settings.SourceLocaleCode = sourceLocale;
                settings.TargetLocaleCode = targetLocale;
                var scan = StringTableScanner.ScanProject(sourceLocale);
                if (scan.EntryCount == 0)
                {
                    FinishBatchReport(output, BreadTranslatorBatchReport.Failure(commandName, 2, "source_entries_missing"));
                    return;
                }
                var xliff = XliffAdapter.BuildXliff20(scan, targetLocale);
                operation = BreadTranslatorApiOperation.Create("xliff-push", ReadArgument("-breadTranslatorIdempotencyKey"));
                var complete = AwaitApiCompletion(commandName, output, operation);
                BreadTranslatorApiClient.PushXliff(settings, xliff, operation, result => complete(ApiReport(commandName, operation, result, new[]
                {
                    new BreadTranslatorBatchStep { name = "scan", status = "succeeded", changed = false },
                    new BreadTranslatorBatchStep { name = "push", status = result.Success ? "succeeded" : "failed", changed = result.Success }
                }, new BreadTranslatorBatchArtifact { kind = "xliff", checksum = Sha256(xliff) })));
            }
            catch (Exception error)
            {
                operation?.Cancel();
                Debug.LogError($"BreadTranslator push failed: {error.GetType().Name}.");
                FinishBatchReport(output, BreadTranslatorBatchReport.Failure(commandName, InvalidInputExitCode, "push_preflight_failed"));
            }
        }

        public static void PullApprovedAndApply()
        {
            const string commandName = "pull-approved-apply";
            var output = ReportOutput();
            BreadTranslatorApiOperation operation = null;
            try
            {
                var settings = BreadTranslatorSettingsProvider.LoadOrCreateSettings();
                if (!HasEnvironmentToken())
                {
                    FinishBatchReport(output, BreadTranslatorBatchReport.Failure(commandName, InvalidInputExitCode, "connector_token_environment_missing"));
                    return;
                }
                settings.SourceLocaleCode = ReadArgument("-breadTranslatorSourceLocale") ?? settings.SourceLocaleCode;
                settings.TargetLocaleCode = ReadArgument("-breadTranslatorTargetLocale") ?? settings.TargetLocaleCode;
                operation = BreadTranslatorApiOperation.Create("xliff-pull", ReadArgument("-breadTranslatorIdempotencyKey"));
                var complete = AwaitApiCompletion(commandName, output, operation);
                BreadTranslatorApiClient.PullApprovedXliff(settings, operation, result =>
                {
                    if (!result.Success)
                    {
                        complete(ApiReport(commandName, operation, result, new[] { new BreadTranslatorBatchStep { name = "pull-verify", status = "failed" } }, null));
                        return;
                    }
                    try
                    {
                        // The API client has already verified the server checksum and both locales.
                        var apply = XliffAdapter.Apply(result.Xliff, settings.TargetLocaleCode);
                        var conflict = apply.HasConflicts;
                        complete(new BreadTranslatorBatchReport
                        {
                            command = commandName,
                            success = !conflict,
                            noOp = !conflict && apply.Applied == 0,
                            exitCode = conflict ? ApplyConflictExitCode : 0,
                            errorCode = conflict ? "apply_conflict" : null,
                            httpStatus = result.StatusCode,
                            idempotencyKeyFingerprint = operation.IdempotencyKeyFingerprint,
                            steps = new[]
                            {
                                new BreadTranslatorBatchStep { name = "pull-verify", status = "succeeded" },
                                new BreadTranslatorBatchStep { name = "atomic-apply", status = conflict ? "blocked" : "succeeded", changed = apply.Applied > 0 }
                            },
                            blockers = apply.Conflicts.OrderBy(item => item.StableId, StringComparer.Ordinal).ThenBy(item => item.Code, StringComparer.Ordinal)
                                .Select(item => BreadTranslatorBatchBlocker.Create("atomic-apply", item.Code, item.Code)).ToArray(),
                            artifacts = new[] { new BreadTranslatorBatchArtifact { kind = "approved-xliff", checksum = Sha256(result.Xliff) } }
                        });
                    }
                    catch (Exception error)
                    {
                        Debug.LogError($"BreadTranslator approved XLIFF apply failed: {error.GetType().Name}.");
                        complete(BreadTranslatorBatchReport.Failure(commandName, InvalidInputExitCode, "approved_xliff_invalid"));
                    }
                });
            }
            catch (Exception error)
            {
                operation?.Cancel();
                Debug.LogError($"BreadTranslator pull/apply failed: {error.GetType().Name}.");
                FinishBatchReport(output, BreadTranslatorBatchReport.Failure(commandName, InvalidInputExitCode, "pull_apply_preflight_failed"));
            }
        }

        public static void Scan()
        {
            Run(settings =>
            {
                var report = StringTableScanner.ScanProject(ReadArgument("-breadTranslatorSourceLocale") ?? settings.SourceLocaleCode);
                Debug.Log($"BreadTranslator scan completed: tables={report.TableCount}, entries={report.EntryCount}, warnings={report.Warnings.Count}.");
                return report.EntryCount > 0 ? 0 : 2;
            });
        }

        public static void Validate()
        {
            Run(settings =>
            {
                var report = StringTableScanner.ScanProject(ReadArgument("-breadTranslatorSourceLocale") ?? settings.SourceLocaleCode);
                var duplicateIdentity = report.Records.GroupBy(record => record.StableId, StringComparer.Ordinal).Any(group => group.Count() > 1);
                var invalidIdentity = report.Records.Any(record => string.IsNullOrWhiteSpace(record.TableCollectionGuid) || string.IsNullOrWhiteSpace(record.EntryId) || string.IsNullOrWhiteSpace(record.Key));
                var blockers = (report.EntryCount == 0 ? 1 : 0) + (duplicateIdentity ? 1 : 0) + (invalidIdentity ? 1 : 0);
                Debug.Log($"BreadTranslator validation completed: entries={report.EntryCount}, blockers={blockers}, warnings={report.Warnings.Count}.");
                return blockers == 0 ? 0 : 3;
            });
        }

        public static void ExportXliff12()
        {
            Export(false);
        }

        public static void ExportXliff20()
        {
            Export(true);
        }

        public static void ExportCsv()
        {
            Run(settings =>
            {
                var sourceLocale = ReadArgument("-breadTranslatorSourceLocale") ?? settings.SourceLocaleCode;
                var targetLocale = ReadArgument("-breadTranslatorTargetLocale") ?? settings.TargetLocaleCode;
                var output = ReadArgument("-breadTranslatorOutput");
                if (string.IsNullOrWhiteSpace(output)) return InvalidInputExitCode;
                var report = StringTableScanner.ScanProject(sourceLocale);
                if (report.EntryCount == 0) return 2;
                var directory = Path.GetDirectoryName(Path.GetFullPath(output));
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(output, CsvAdapter.ExportCsv(report, targetLocale), new UTF8Encoding(false));
                return 0;
            });
        }

        public static void ApplyXliff()
        {
            Run(settings =>
            {
                var input = ReadArgument("-breadTranslatorInput");
                if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
                {
                    Debug.LogError("BreadTranslator apply requires -breadTranslatorInput <file.xlf>.");
                    return 4;
                }
                var targetLocale = ReadArgument("-breadTranslatorTargetLocale") ?? settings.TargetLocaleCode;
                var report = XliffAdapter.Apply(File.ReadAllText(input), targetLocale);
                Debug.Log($"BreadTranslator apply completed: applied={report.Applied}, unchanged={report.Unchanged}, conflicts={report.Conflicts.Count}.");
                return report.HasConflicts ? 5 : 0;
            });
        }

        public static void ApplyCsv()
        {
            Run(settings =>
            {
                var input = ReadArgument("-breadTranslatorInput");
                if (string.IsNullOrWhiteSpace(input) || !File.Exists(input)) return InvalidInputExitCode;
                var targetLocale = ReadArgument("-breadTranslatorTargetLocale") ?? settings.TargetLocaleCode;
                var report = CsvAdapter.ApplyCsv(File.ReadAllText(input), targetLocale);
                Debug.Log($"BreadTranslator CSV apply completed: applied={report.Applied}, unchanged={report.Unchanged}, conflicts={report.Conflicts.Count}.");
                return report.HasConflicts ? ApplyConflictExitCode : 0;
            });
        }

        public static void CaptureContextReport()
        {
            const string commandName = "capture-context";
            var output = ReadArgument("-breadTranslatorReport") ?? ReadArgument("-breadTranslatorOutput");
            var exitCode = 0;
            BreadTranslatorBatchReport report;
            try
            {
                var settings = BreadTranslatorSettingsProvider.LoadOrCreateSettings();
                var scan = StringTableScanner.ScanProject(ReadArgument("-breadTranslatorSourceLocale") ?? settings.SourceLocaleCode);
                var assetScan = AssetTableScanner.ScanProject(ReadArgument("-breadTranslatorSourceLocale") ?? settings.SourceLocaleCode);
                report = new BreadTranslatorBatchReport
                {
                    command = commandName,
                    success = true,
                    exitCode = 0,
                    context = BreadTranslatorContextCapture.Collect(settings, null, scan.Records, assetScan.Records)
                };
                BreadTranslatorBatchReport.Write(output, report);
                Debug.Log($"BreadTranslator context report completed: stringEntries={report.context.candidateStringRefs.Length}, assetEntries={report.context.candidateAssetRefs.Length}.");
            }
            catch (Exception error)
            {
                exitCode = 6;
                report = BreadTranslatorBatchReport.Failure(commandName, exitCode, "context_report_failed");
                TryWriteReport(output, report);
                Debug.LogError($"BreadTranslator context report failed: {error.GetType().Name}.");
            }
            if (Application.isBatchMode) EditorApplication.Exit(exitCode);
        }

        public static void ReportLqaIssue()
        {
            const string commandName = "report-lqa";
            var output = ReadArgument("-breadTranslatorReport") ?? ReadArgument("-breadTranslatorOutput");
            try
            {
                var screenshotPath = ReadArgument("-breadTranslatorScreenshot");
                if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath))
                {
                    FinishBatchReport(output, BreadTranslatorBatchReport.Failure(commandName, 4, "screenshot_missing"));
                    return;
                }
                var bytes = File.ReadAllBytes(screenshotPath);
                if (bytes.Length == 0 || bytes.Length > 25 * 1024 * 1024)
                {
                    FinishBatchReport(output, BreadTranslatorBatchReport.Failure(commandName, 4, "screenshot_size_invalid"));
                    return;
                }
                var settings = BreadTranslatorSettingsProvider.LoadOrCreateSettings();
                var scan = StringTableScanner.ScanProject(ReadArgument("-breadTranslatorSourceLocale") ?? settings.SourceLocaleCode);
                var context = BreadTranslatorContextCapture.Collect(settings, null, scan.Records);
                var draft = new BreadTranslatorLqaIssueDraft
                {
                    title = ReadArgument("-breadTranslatorIssueTitle") ?? "Unity batchmode localization issue",
                    category = ReadArgument("-breadTranslatorIssueCategory") ?? "layout",
                    severity = ReadArgument("-breadTranslatorIssueSeverity") ?? "major",
                    description = ReadArgument("-breadTranslatorIssueDescription") ?? ""
                };
                var operation = BreadTranslatorApiOperation.Create("lqa-report", ReadArgument("-breadTranslatorIdempotencyKey"));
                BreadTranslatorApiClient.ReportLqaIssue(settings, bytes, context, draft, operation, result =>
                {
                    FinishBatchReport(output, new BreadTranslatorBatchReport
                    {
                        command = commandName,
                        success = result.Success,
                        exitCode = result.Success ? 0 : 7,
                        errorCode = result.ErrorCode,
                        httpStatus = result.StatusCode,
                        artifactId = result.ArtifactId,
                        issueId = result.IssueId,
                        idempotencyKeyFingerprint = operation.IdempotencyKeyFingerprint,
                        context = context
                    });
                });
            }
            catch (Exception error)
            {
                Debug.LogError($"BreadTranslator LQA report failed: {error.GetType().Name}.");
                FinishBatchReport(output, BreadTranslatorBatchReport.Failure(commandName, 7, "lqa_report_failed"));
            }
        }

        private static void Export(bool version20)
        {
            Run(settings =>
            {
                var sourceLocale = ReadArgument("-breadTranslatorSourceLocale") ?? settings.SourceLocaleCode;
                var targetLocale = ReadArgument("-breadTranslatorTargetLocale") ?? settings.TargetLocaleCode;
                var output = ReadArgument("-breadTranslatorOutput");
                if (string.IsNullOrWhiteSpace(output))
                {
                    Debug.LogError("BreadTranslator export requires -breadTranslatorOutput <file.xlf>.");
                    return 4;
                }
                var report = StringTableScanner.ScanProject(sourceLocale);
                if (report.EntryCount == 0) return 2;
                var directory = Path.GetDirectoryName(Path.GetFullPath(output));
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var content = version20 ? XliffAdapter.BuildXliff20(report, targetLocale) : XliffAdapter.BuildXliff12(report, targetLocale);
                File.WriteAllText(output, content, new System.Text.UTF8Encoding(false));
                Debug.Log($"BreadTranslator XLIFF export completed: version={(version20 ? "2.0" : "1.2")}, entries={report.EntryCount}.");
                return 0;
            });
        }

        private static void Run(Func<BreadTranslatorProjectSettings, int> command)
        {
            var exitCode = 1;
            try
            {
                exitCode = command(BreadTranslatorSettingsProvider.LoadOrCreateSettings());
            }
            catch (Exception error)
            {
                Debug.LogError($"BreadTranslator batch command failed: {error.GetType().Name}.");
            }
            if (Application.isBatchMode) EditorApplication.Exit(exitCode);
        }

        internal static int ExitCodeForApiFailure(long statusCode) => statusCode == 409 ? RemoteConflictExitCode : HttpFailureExitCode;

        private static Action<BreadTranslatorBatchReport> AwaitApiCompletion(string commandName, string output, BreadTranslatorApiOperation operation)
        {
            var completed = false;
            var deadline = EditorApplication.timeSinceStartup + ReadTimeoutSeconds();
            EditorApplication.CallbackFunction watchdog = null;
            Action<BreadTranslatorBatchReport> finish = report =>
            {
                if (completed) return;
                completed = true;
                EditorApplication.update -= watchdog;
                FinishBatchReport(output, report);
            };
            watchdog = () =>
            {
                if (completed || EditorApplication.timeSinceStartup < deadline) return;
                operation.Cancel();
                var report = BreadTranslatorBatchReport.Failure(commandName, TimeoutExitCode, "connector_request_timeout");
                report.idempotencyKeyFingerprint = operation.IdempotencyKeyFingerprint;
                finish(report);
            };
            EditorApplication.update += watchdog;
            return finish;
        }

        private static BreadTranslatorBatchReport ApiReport(string commandName, BreadTranslatorApiOperation operation, BreadTranslatorApiResult result, BreadTranslatorBatchStep[] steps, BreadTranslatorBatchArtifact artifact)
        {
            var success = result != null && result.Success;
            var errorCode = success ? null : BreadTranslatorBatchReport.Sanitize(result?.ErrorCode ?? "connector_request_failed");
            return new BreadTranslatorBatchReport
            {
                command = commandName,
                success = success,
                noOp = !success,
                exitCode = success ? 0 : ExitCodeForApiFailure(result?.StatusCode ?? 0),
                errorCode = errorCode,
                httpStatus = result?.StatusCode ?? 0,
                idempotencyKeyFingerprint = operation.IdempotencyKeyFingerprint,
                steps = steps ?? Array.Empty<BreadTranslatorBatchStep>(),
                blockers = success ? Array.Empty<BreadTranslatorBatchBlocker>() : new[] { BreadTranslatorBatchBlocker.Create(commandName, errorCode, "The remote connector request failed.") },
                artifacts = artifact == null ? Array.Empty<BreadTranslatorBatchArtifact>() : new[] { artifact }
            };
        }

        private static string ReportOutput() => ReadArgument("-breadTranslatorReport") ?? ReadArgument("-breadTranslatorOutput");
        private static bool HasEnvironmentToken() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BREADTRANSLATOR_TOKEN"));
        private static double ReadTimeoutSeconds()
        {
            int seconds;
            return int.TryParse(ReadArgument("-breadTranslatorTimeoutSeconds"), out seconds) && seconds >= 5 && seconds <= 600 ? seconds : 45;
        }
        private static string Sha256(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
        }

        private static string ReadArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var index = 0; index < args.Length - 1; index += 1)
            {
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
            }
            return null;
        }

        private static void FinishBatchReport(string output, BreadTranslatorBatchReport report)
        {
            TryWriteReport(output, report);
            Debug.Log($"BreadTranslator batch report completed: command={report.command}, success={report.success}, code={report.errorCode ?? "none"}, idempotency={BreadTranslatorBatchReport.SafeIdempotencyFingerprint(report.idempotencyKeyFingerprint) ?? "none"}.");
            if (Application.isBatchMode) EditorApplication.Exit(report.exitCode);
        }

        private static void TryWriteReport(string output, BreadTranslatorBatchReport report)
        {
            try { BreadTranslatorBatchReport.Write(output, report); }
            catch (Exception error) { Debug.LogError($"BreadTranslator JSON report write failed: {error.GetType().Name}."); }
        }
    }
}
