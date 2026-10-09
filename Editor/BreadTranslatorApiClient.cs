using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using BreadTranslator.Unity;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace BreadTranslator.Unity.Editor
{
    public static class BreadTranslatorApiClient
    {
        public static BreadTranslatorApiOperation ReportLqaIssue(
            BreadTranslatorProjectSettings settings,
            byte[] pngBytes,
            BreadTranslatorCaptureContext context,
            BreadTranslatorLqaIssueDraft issue,
            Action<BreadTranslatorLqaReportResult> completed)
        {
            var operation = BreadTranslatorApiOperation.Create("lqa-report");
            return ReportLqaIssue(settings, pngBytes, context, issue, operation, completed);
        }

        public static BreadTranslatorApiOperation ReportLqaIssue(
            BreadTranslatorProjectSettings settings,
            byte[] pngBytes,
            BreadTranslatorCaptureContext context,
            BreadTranslatorLqaIssueDraft issue,
            BreadTranslatorApiOperation operation,
            Action<BreadTranslatorLqaReportResult> completed)
        {
            operation = RequireActiveOperation(operation, "lqa-report");
            if (pngBytes == null || pngBytes.Length == 0)
            {
                completed?.Invoke(BreadTranslatorLqaReportResult.Failed("screenshot_empty", 0));
                return operation;
            }
            if (string.IsNullOrWhiteSpace(BreadTranslatorSettingsProvider.GetToken()))
            {
                completed?.Invoke(BreadTranslatorLqaReportResult.Failed("connector_token_missing", 0));
                return operation;
            }
            if (string.IsNullOrWhiteSpace(BreadTranslatorSettingsProvider.GetLqaToken()))
            {
                completed?.Invoke(BreadTranslatorLqaReportResult.Failed("lqa_token_missing", 0));
                return operation;
            }
            if (Application.internetReachability == NetworkReachability.NotReachable)
            {
                completed?.Invoke(BreadTranslatorLqaReportResult.Failed("offline", 0));
                return operation;
            }

            var filename = $"unity-lqa-{DateTime.UtcNow:yyyyMMdd-HHmmss}.png";
            var metadata = ContextMetadata(settings, context);
            var createPayload = "{" +
                JsonProperty("filename", filename) + "," +
                JsonProperty("contentType", "image/png") + "," +
                JsonProperty("kind", "lqa_screenshot") + "," +
                JsonProperty("format", "png") + "," +
                JsonNumberProperty("byteSize", pngBytes.Length) + "," +
                JsonProperty("engine", "unity") + "," +
                JsonProperty("targetLocale", context?.locale) + "," +
                JsonProperty("branch", context?.branch) + "," +
                "\"metadata\":" + MetadataJson(metadata, context?.candidateStringRefs, context?.candidateAssetRefs) + "}";

            Send(settings, "/artifacts/upload-sessions", "POST", Encoding.UTF8.GetBytes(createPayload), "application/json", operation, response =>
            {
                if (!response.Success)
                {
                    completed?.Invoke(BreadTranslatorLqaReportResult.Failed(response.ErrorCode, response.StatusCode));
                    return;
                }
                ArtifactUploadSessionResponse session;
                try { session = JsonUtility.FromJson<ArtifactUploadSessionResponse>(response.ResponseBody); }
                catch { session = null; }
                if (session?.uploadSession == null || string.IsNullOrWhiteSpace(session.uploadSession.uploadUrl) || string.IsNullOrWhiteSpace(session.uploadSession.artifactId))
                {
                    completed?.Invoke(BreadTranslatorLqaReportResult.Failed("invalid_upload_session", response.StatusCode));
                    return;
                }

                SendAbsolute(settings, session.uploadSession.uploadUrl, "PUT", pngBytes, "image/png", operation, upload =>
                {
                    if (!upload.Success)
                    {
                        completed?.Invoke(BreadTranslatorLqaReportResult.Failed(upload.ErrorCode, upload.StatusCode));
                        return;
                    }
                    var issuePayload = BuildLqaPayload(issue, context, session.uploadSession.artifactId, metadata);
                    Send(settings, "/lqa/issues", "POST", Encoding.UTF8.GetBytes(issuePayload), "application/json", operation, created =>
                    {
                        if (!created.Success)
                        {
                            completed?.Invoke(BreadTranslatorLqaReportResult.Failed(created.ErrorCode, created.StatusCode, session.uploadSession.artifactId));
                            return;
                        }
                        LqaCreateResponse parsed;
                        try { parsed = JsonUtility.FromJson<LqaCreateResponse>(created.ResponseBody); }
                        catch { parsed = null; }
                        operation.CompleteSuccessfully();
                        completed?.Invoke(new BreadTranslatorLqaReportResult(
                            true,
                            created.StatusCode,
                            null,
                            session.uploadSession.artifactId,
                            parsed?.issue?.id));
                    }, true);
                });
            });
            return operation;
        }

        public static BreadTranslatorApiOperation PushXliff(BreadTranslatorProjectSettings settings, string xliff, Action<BreadTranslatorApiResult> completed)
        {
            return PushXliff(settings, xliff, BreadTranslatorApiOperation.Create("xliff-push"), completed);
        }

        public static BreadTranslatorApiOperation PushXliff(BreadTranslatorProjectSettings settings, string xliff, BreadTranslatorApiOperation operation, Action<BreadTranslatorApiResult> completed)
        {
            operation = RequireActiveOperation(operation, "xliff-push");
            var document = XDocument.Parse(xliff);
            var units = document.Descendants().Where(e => e.Name.LocalName == "trans-unit" && e.Elements().Any(x => x.Name.LocalName == "source" && x.Value.Length > 0)).ToArray();
            if (units.Length == 0) throw new ArgumentException("Push requires XLIFF 1.2 String Table units.");
            var chunks = new List<string>();
            for (var index = 0; index < units.Length; index += 25)
            {
                var chunk = new XDocument(document);
                var keep = new HashSet<string>(units.Skip(index).Take(25).Select(e => (string)e.Attribute(XName.Get("stable-id", "urn:breadtranslator:unity:1"))));
                foreach (var unit in chunk.Descendants().Where(e => e.Name.LocalName == "trans-unit").ToArray())
                    if (!keep.Contains((string)unit.Attribute(XName.Get("stable-id", "urn:breadtranslator:unity:1")))) unit.Remove();
                var payload = JsonUtility.ToJson(new UnityXliffImportPayload { targetLocale = settings.TargetLocaleCode, sourceLocale = settings.SourceLocaleCode, branch = settings.Branch, format = "xliff", xliff = chunk.ToString(SaveOptions.DisableFormatting) });
                if (Encoding.UTF8.GetByteCount(payload) > 900000) throw new ArgumentException("A String Table chunk exceeds the upload limit. Split unusually large entries.");
                chunks.Add(payload);
            }
            var current = 0;
            Action next = null;
            next = () => Send(settings, "/connectors/unity/xliff/import/commit", "POST", chunks[current], response =>
            {
                if (!response.Success) { completed?.Invoke(response); return; }
                if (operation.IsCancelled) { completed?.Invoke(new BreadTranslatorApiResult(false, 0, "cancelled", null)); return; }
                if (++current < chunks.Count && !operation.IsCancelled) { next(); return; }
                operation.CompleteSuccessfully();
                completed?.Invoke(new BreadTranslatorApiResult(true, response.StatusCode, null, null));
            }, operation);
            next();
            return operation;
        }

        public static BreadTranslatorApiOperation PullApprovedXliff(BreadTranslatorProjectSettings settings, Action<BreadTranslatorApiResult> completed)
        {
            return PullApprovedXliff(settings, BreadTranslatorApiOperation.Create("xliff-pull"), completed);
        }

        public static BreadTranslatorApiOperation PullApprovedXliff(BreadTranslatorProjectSettings settings, BreadTranslatorApiOperation operation, Action<BreadTranslatorApiResult> completed)
        {
            operation = RequireActiveOperation(operation, "xliff-pull");
            var payload = JsonUtility.ToJson(new UnityXliffExportPayload
            {
                targetLocale = settings.TargetLocaleCode,
                branch = settings.Branch,
                status = "approved",
                format = "xliff"
            });
            Send(settings, "/connectors/unity/xliff/export", "POST", payload, response =>
            {
                if (!response.Success)
                {
                    completed?.Invoke(response);
                    return;
                }
                UnityXliffExportResponse parsed;
                try
                {
                    parsed = JsonUtility.FromJson<UnityXliffExportResponse>(response.ResponseBody);
                }
                catch
                {
                    parsed = null;
                }
                if (parsed == null || string.IsNullOrWhiteSpace(parsed.xliff))
                {
                    completed?.Invoke(new BreadTranslatorApiResult(false, response.StatusCode, "invalid_export_response", null));
                    return;
                }
                try
                {
                    UnityArtifactVerifier.VerifyExport(
                        parsed.xliff,
                        parsed.artifact?.checksumSha256,
                        settings.SourceLocaleCode,
                        settings.TargetLocaleCode);
                }
                catch (UnityArtifactVerificationException error)
                {
                    completed?.Invoke(new BreadTranslatorApiResult(false, response.StatusCode, error.Code, null));
                    return;
                }
                operation.CompleteSuccessfully();
                completed?.Invoke(new BreadTranslatorApiResult(true, response.StatusCode, null, parsed.xliff));
            }, operation);
            return operation;
        }

        private static void Send(
            BreadTranslatorProjectSettings settings,
            string relativePath,
            string method,
            string json,
            Action<InternalApiResponse> completed,
            BreadTranslatorApiOperation operation = null)
        {
            Send(settings, relativePath, method, Encoding.UTF8.GetBytes(json ?? "{}"), "application/json", RequireActiveOperation(operation, "api-request"), completed);
        }

        private static void Send(
            BreadTranslatorProjectSettings settings,
            string relativePath,
            string method,
            byte[] body,
            string contentType,
            BreadTranslatorApiOperation cancellation,
            Action<InternalApiResponse> completed,
            bool useLqaToken = false)
        {
            string endpoint;
            try { endpoint = BuildProjectEndpoint(settings, relativePath); }
            catch (ArgumentException) { completed?.Invoke(new InternalApiResponse(false, 0, "invalid_https_endpoint", null)); return; }
            SendAbsolute(settings, endpoint, method, body, contentType, cancellation, completed, useLqaToken);
        }

        private static void SendAbsolute(
            BreadTranslatorProjectSettings settings,
            string endpoint,
            string method,
            byte[] body,
            string contentType,
            BreadTranslatorApiOperation cancellation,
            Action<InternalApiResponse> completed,
            bool useLqaToken = false)
        {
            var token = useLqaToken ? BreadTranslatorSettingsProvider.GetLqaToken() : BreadTranslatorSettingsProvider.GetToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                completed?.Invoke(new InternalApiResponse(false, 0, useLqaToken ? "lqa_token_missing" : "connector_token_missing", null));
                return;
            }
            if (string.IsNullOrWhiteSpace(settings.WorkspaceSlug) || string.IsNullOrWhiteSpace(settings.ProjectSlug))
            {
                completed?.Invoke(new InternalApiResponse(false, 0, "connector_project_missing", null));
                return;
            }

            try { BreadLingoEndpoint.Validate(settings, endpoint); }
            catch (ArgumentException) { completed?.Invoke(new InternalApiResponse(false, 0, "invalid_https_endpoint", null)); return; }
            var request = new UnityWebRequest(endpoint, method)
            {
                uploadHandler = new UploadHandlerRaw(body ?? Array.Empty<byte>()),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 30,
                redirectLimit = 0
            };
            request.SetRequestHeader("Content-Type", contentType);
            if (useLqaToken) request.SetRequestHeader("Authorization", "Bearer " + token);
            else request.SetRequestHeader("X-Connector-Token", token);
            using (var sha = SHA256.Create())
                request.SetRequestHeader("Idempotency-Key", cancellation.IdempotencyKey + "-" + BitConverter.ToString(sha.ComputeHash(body ?? Array.Empty<byte>()), 0, 8).Replace("-", ""));
            cancellation?.Attach(request);
            var operation = request.SendWebRequest();
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (!operation.isDone) return;
                EditorApplication.update -= tick;
                cancellation?.Detach(request);
                var success = request.result == UnityWebRequest.Result.Success;
                var responseBody = success ? request.downloadHandler.text : null;
                var errorCode = success ? null
                    : cancellation != null && cancellation.IsCancelled ? "cancelled"
                    : request.responseCode == 401 || request.responseCode == 403 ? "connector_access_denied"
                    : request.result == UnityWebRequest.Result.ConnectionError ? "offline_or_network_error"
                    : "connector_request_failed";
                try { completed?.Invoke(new InternalApiResponse(success, request.responseCode, errorCode, responseBody)); }
                finally { request.Dispose(); }
            };
            EditorApplication.update += tick;
        }

        private static string BuildProjectEndpoint(BreadTranslatorProjectSettings settings, string relativePath)
        {
            var path = $"/api/workspaces/{Uri.EscapeDataString(settings.WorkspaceSlug)}/projects/{Uri.EscapeDataString(settings.ProjectSlug)}{relativePath}";
            return BreadLingoEndpoint.Build(settings, path);
        }

        private static BreadTranslatorApiOperation RequireActiveOperation(BreadTranslatorApiOperation operation, string scope)
        {
            operation = operation ?? BreadTranslatorApiOperation.Create(scope);
            if (!operation.IsActive) throw new InvalidOperationException("A completed or cancelled BreadTranslator API operation cannot be reused.");
            return operation;
        }

        private static List<BreadTranslatorContextField> ContextMetadata(BreadTranslatorProjectSettings settings, BreadTranslatorCaptureContext context)
        {
            var allowlist = BreadTranslatorMetadataPolicy.ParseAllowlist(settings?.ContextMetadataAllowlist);
            return BreadTranslatorMetadataPolicy.Filter(context?.metadata, allowlist).ToList();
        }

        private static string BuildLqaPayload(BreadTranslatorLqaIssueDraft issue, BreadTranslatorCaptureContext context, string artifactId, List<BreadTranslatorContextField> metadata)
        {
            issue = issue ?? new BreadTranslatorLqaIssueDraft();
            return "{" +
                JsonProperty("title", issue.title) + "," +
                JsonProperty("category", issue.category) + "," +
                JsonProperty("severity", issue.severity) + "," +
                JsonProperty("description", issue.description) + "," +
                JsonProperty("targetLocale", context?.locale) + "," +
                JsonProperty("buildId", context?.buildVersion) + "," +
                JsonProperty("platform", context?.buildTarget) + "," +
                JsonProperty("screenshotArtifactId", artifactId) + "," +
                "\"metadata\":" + MetadataJson(metadata, context?.candidateStringRefs, context?.candidateAssetRefs) + "}";
        }

        internal static string MetadataJson(
            IEnumerable<BreadTranslatorContextField> fields,
            IEnumerable<BreadTranslatorStringReference> references = null,
            IEnumerable<BreadTranslatorAssetReference> assetReferences = null)
        {
            var properties = (fields ?? Array.Empty<BreadTranslatorContextField>())
                .Where(field => field != null && BreadTranslatorMetadataPolicy.IsValidKey(field.key))
                .Select(field => $"{Quote(field.key)}:{Quote(field.value)}")
                .ToList();
            var refs = (references ?? Array.Empty<BreadTranslatorStringReference>()).Where(item => item != null).ToArray();
            if (refs.Length > 0)
            {
                properties.Add("\"candidateStringRefs\":[" + string.Join(",", refs.Select(item => "{" +
                    JsonProperty("tableCollectionGuid", item.tableCollectionGuid) + "," +
                    JsonProperty("entryId", item.entryId) + "," +
                    JsonProperty("key", item.key) + "}")) + "]");
            }
            var assetRefs = (assetReferences ?? Array.Empty<BreadTranslatorAssetReference>())
                .Where(item => item != null)
                .OrderBy(item => item.StableId, StringComparer.Ordinal)
                .ToArray();
            if (assetRefs.Length > 0)
            {
                properties.Add("\"candidateAssetRefs\":[" + string.Join(",", assetRefs.Select(item => "{" +
                    JsonProperty("tableCollectionGuid", item.tableCollectionGuid) + "," +
                    JsonProperty("entryId", item.entryId) + "," +
                    JsonProperty("key", item.key) + "}")) + "]");
            }
            return "{" + string.Join(",", properties) + "}";
        }

        private static string JsonProperty(string key, string value) => $"{Quote(key)}:{Quote(value)}";
        private static string JsonNumberProperty(string key, long value) => $"{Quote(key)}:{value}";
        private static string Quote(string value)
        {
            var escaped = (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return $"\"{escaped}\"";
        }

        [Serializable]
        private sealed class UnityXliffImportPayload
        {
            public string targetLocale;
            public string sourceLocale;
            public string branch;
            public string format;
            public string xliff;
        }

        [Serializable]
        private sealed class UnityXliffExportPayload
        {
            public string targetLocale;
            public string branch;
            public string status;
            public string format;
        }

        [Serializable]
        private sealed class UnityXliffExportResponse
        {
            public string xliff;
            public UnityXliffExportArtifact artifact;
        }

        [Serializable]
        private sealed class UnityXliffExportArtifact { public string checksumSha256; }

        [Serializable]
        private sealed class ArtifactUploadSessionResponse { public ArtifactUploadSession uploadSession; }
        [Serializable]
        private sealed class ArtifactUploadSession { public string artifactId; public string uploadUrl; }
        [Serializable]
        private sealed class LqaCreateResponse { public LqaIssue issue; }
        [Serializable]
        private sealed class LqaIssue { public string id; }

        private sealed class InternalApiResponse : BreadTranslatorApiResult
        {
            public InternalApiResponse(bool success, long statusCode, string errorCode, string responseBody)
                : base(success, statusCode, errorCode, null)
            {
                ResponseBody = responseBody;
            }

            public string ResponseBody { get; }
        }
    }

    public sealed class BreadTranslatorApiOperation
    {
        private readonly HashSet<UnityWebRequest> requests = new HashSet<UnityWebRequest>();
        private readonly string idempotencyKey;

        private BreadTranslatorApiOperation(string scope, string callerKey)
        {
            Scope = NormalizeScope(scope);
            idempotencyKey = string.IsNullOrWhiteSpace(callerKey)
                ? $"unity-editor-{Scope}-{Guid.NewGuid():N}"
                : ValidateCallerKey(callerKey);
            IdempotencyKeyFingerprint = Fingerprint(idempotencyKey);
            State = BreadTranslatorApiOperationState.Active;
        }

        public string Scope { get; }
        public string IdempotencyKeyFingerprint { get; }
        public BreadTranslatorApiOperationState State { get; private set; }
        public bool IsActive => State == BreadTranslatorApiOperationState.Active;
        public bool IsCancelled => State == BreadTranslatorApiOperationState.Cancelled;
        internal string IdempotencyKey => idempotencyKey;

        public static BreadTranslatorApiOperation Create(string scope, string callerKey = null) =>
            new BreadTranslatorApiOperation(scope, callerKey);

        internal void Attach(UnityWebRequest request) { if (request != null) requests.Add(request); if (IsCancelled) request?.Abort(); }
        internal void Detach(UnityWebRequest request) { if (request != null) requests.Remove(request); }
        internal void CompleteSuccessfully()
        {
            if (!IsActive) return;
            State = BreadTranslatorApiOperationState.Succeeded;
            requests.Clear();
        }
        public void Cancel()
        {
            if (!IsActive) return;
            State = BreadTranslatorApiOperationState.Cancelled;
            foreach (var request in requests.ToArray()) request?.Abort();
            requests.Clear();
        }

        private static string NormalizeScope(string scope)
        {
            var normalized = new string((scope ?? "operation").Trim().ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) || character == '-' ? character : '-')
                .ToArray()).Trim('-');
            return string.IsNullOrWhiteSpace(normalized) ? "operation" : normalized;
        }

        private static string ValidateCallerKey(string value)
        {
            value = value.Trim();
            if (value.Length < 16 || value.Length > 200 || value.Any(character => character <= 0x20 || character >= 0x7f))
                throw new ArgumentException("The idempotency key must contain 16-200 printable ASCII characters without whitespace.", nameof(value));
            return value;
        }

        private static string Fingerprint(string value)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                return "idemfp_" + BitConverter.ToString(hash, 0, 12).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }

    public enum BreadTranslatorApiOperationState { Active, Succeeded, Cancelled }

    [Serializable]
    public sealed class BreadTranslatorLqaIssueDraft
    {
        public string title = "Unity localization issue";
        public string category = "layout";
        public string severity = "major";
        public string description = "";
    }

    public sealed class BreadTranslatorLqaReportResult
    {
        public BreadTranslatorLqaReportResult(bool success, long statusCode, string errorCode, string artifactId, string issueId)
        {
            Success = success; StatusCode = statusCode; ErrorCode = errorCode; ArtifactId = artifactId; IssueId = issueId;
        }
        public bool Success { get; }
        public long StatusCode { get; }
        public string ErrorCode { get; }
        public string ArtifactId { get; }
        public string IssueId { get; }
        public static BreadTranslatorLqaReportResult Failed(string code, long statusCode, string artifactId = null) =>
            new BreadTranslatorLqaReportResult(false, statusCode, code ?? "unknown_error", artifactId, null);
    }

    public class BreadTranslatorApiResult
    {
        public BreadTranslatorApiResult(bool success, long statusCode, string errorCode, string xliff)
        {
            Success = success;
            StatusCode = statusCode;
            ErrorCode = errorCode;
            Xliff = xliff;
        }

        public bool Success { get; }
        public long StatusCode { get; }
        public string ErrorCode { get; }
        public string Xliff { get; }
    }
}
