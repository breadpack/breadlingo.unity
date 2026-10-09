using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace BreadTranslator.Unity
{
    [Serializable] public sealed class InGameQaConsent { public bool granted; public string grantedAt; public string policyVersion; public string[] categories; }
    [Serializable] public sealed class InGameQaBuild { public string id; public string version; public string branch; }
    [Serializable] public sealed class InGameQaDevice { public string idHash; public string model; public string os; }
    [Serializable] public sealed class InGameQaLocation { public string scene; public string map; public string widget; }
    [Serializable] public sealed class InGameQaString { public string key; public string table; public string source; public string rendered; }
    [Serializable] public sealed class InGameQaArtifact { public string localId; public string kind; public string contentType; public int byteSize; public string sha256; }
    [Serializable] public sealed class InGameQaIssue { public string title; public string description; public string category; public string severity; }
    [Serializable] public sealed class InGameQaTelemetry { public float fps; public float frameTimeMs; public int memoryMb; public int screenWidth; public int screenHeight; public string network; public string quality; public string orientation; }
    [Serializable] public sealed class InGameQaPoint { public float x; public float y; }
    [Serializable] public sealed class InGameQaAnnotationShape { public string kind; public float x; public float y; public float width; public float height; public float endX; public float endY; public InGameQaPoint[] points; }
    [Serializable] public sealed class InGameQaAnnotation { public string id; public InGameQaAnnotationShape shape; public string category; public string comment; public string[] issueIds; }
    [Serializable] public sealed class InGameQaAnnotations { public int imageWidth; public int imageHeight; public int baseRevision; public InGameQaAnnotation[] annotations; }

    [Serializable]
    public sealed class InGameQaEnvelope
    {
        public string schema = "breadtranslator.ingame-qa.v1";
        public string eventId;
        public string sessionId;
        public string capturedAt;
        public string engine = "unity";
        public InGameQaBuild build;
        public InGameQaDevice device;
        public string platform;
        public string locale;
        public InGameQaLocation location;
        public InGameQaString[] strings;
        public InGameQaArtifact[] artifacts;
        public InGameQaAnnotations annotations;
        public InGameQaIssue issue;
        public InGameQaTelemetry telemetry;
        public InGameQaConsent consent;
    }

    public sealed class BreadTranslatorInGameQa : MonoBehaviour
    {
        public const int MaxQueueItems = 100;
        public const int MaxScreenshotBytes = 8 * 1024 * 1024;
        public const int MaxEnvelopeBytes = 64 * 1024;
        private const int MaxAttempts = 8;
        private static readonly string[] SecretMarkers = { "token", "secret", "password", "authorization", "cookie", "credential", "api_key" };

        [SerializeField] private string apiBaseUrl = "https://api.breadtranslator.example.com";
        [SerializeField] private string workspaceSlug = "";
        [SerializeField] private string projectSlug = "";
        [SerializeField] private bool flushOnStart = true;

        // Applications may replace this with a platform keystore callback. Never serialize its result.
        public static Func<string> ConnectorTokenProvider = () => Environment.GetEnvironmentVariable("BREADTRANSLATOR_TOKEN");
        public static Func<string> LqaTokenProvider = () => Environment.GetEnvironmentVariable("BREADTRANSLATOR_LQA_TOKEN");
        private int flushAttempt;
        private bool flushing;

        private string QueueRoot => Path.Combine(Application.persistentDataPath, "BreadTranslator", "InGameQa", "PrivateQueue");

        private void Start() { if (flushOnStart) StartCoroutine(Flush()); }

        public bool Queue(InGameQaEnvelope envelope, byte[] screenshotPng, out string error)
        {
            error = Validate(envelope, screenshotPng);
            if (error != null) return false;
            Directory.CreateDirectory(QueueRoot);
            if (Directory.GetFiles(QueueRoot, "*.json").Length >= MaxQueueItems) { error = "queue_full"; return false; }
            envelope.issue.title = Redact(envelope.issue.title, 160);
            envelope.issue.description = Redact(envelope.issue.description, 4000);
            envelope.strings = (envelope.strings ?? Array.Empty<InGameQaString>()).Take(50).ToArray();
            if (screenshotPng != null && screenshotPng.Length > 0)
            {
                var localId = envelope.eventId + "-screenshot";
                envelope.artifacts = new[] { new InGameQaArtifact { localId = localId, kind = "screenshot", contentType = "image/png", byteSize = screenshotPng.Length, sha256 = Sha256(screenshotPng) } };
            }
            var json = JsonUtility.ToJson(envelope);
            if (Encoding.UTF8.GetByteCount(json) > MaxEnvelopeBytes) { error = "envelope_size_exceeded"; return false; }
            if (screenshotPng != null && screenshotPng.Length > 0) AtomicWrite(Path.Combine(QueueRoot, envelope.artifacts[0].localId + ".png"), screenshotPng);
            AtomicWrite(Path.Combine(QueueRoot, envelope.eventId + ".json"), Encoding.UTF8.GetBytes(json));
            return true;
        }

        public IEnumerator Flush(Action<int, int> completed = null)
        {
            if (flushing) yield break;
            flushing = true;
            Directory.CreateDirectory(QueueRoot);
            var sent = 0; var deferred = 0;
            foreach (var path in Directory.GetFiles(QueueRoot, "*.json").OrderBy(value => value, StringComparer.Ordinal))
            {
                InGameQaEnvelope envelope;
                try { envelope = JsonUtility.FromJson<InGameQaEnvelope>(File.ReadAllText(path)); }
                catch { Quarantine(path); continue; }
                var done = false; var success = false; var retryable = true;
                yield return Send(envelope, (ok, retry) => { success = ok; retryable = retry; done = true; });
                if (!done || !success) { deferred++; if (!retryable) Quarantine(path); continue; }
                File.Delete(path);
                foreach (var artifact in envelope.artifacts ?? Array.Empty<InGameQaArtifact>()) DeleteQuietly(Path.Combine(QueueRoot, artifact.localId + ".png"));
                sent++;
            }
            flushing = false;
            completed?.Invoke(sent, deferred);
            if (deferred > 0 && flushAttempt < MaxAttempts)
            {
                flushAttempt++;
                StartCoroutine(RetryFlush(Mathf.Min(900f, Mathf.Pow(2f, flushAttempt)) + UnityEngine.Random.Range(0f, 1f)));
            }
            else if (deferred == 0) flushAttempt = 0;
        }

        private IEnumerator RetryFlush(float delaySeconds) { yield return new WaitForSecondsRealtime(delaySeconds); yield return Flush(); }

        private IEnumerator Send(InGameQaEnvelope envelope, Action<bool, bool> completed)
        {
            var connector = ConnectorTokenProvider?.Invoke(); var lqa = LqaTokenProvider?.Invoke();
            if (string.IsNullOrWhiteSpace(connector) || string.IsNullOrWhiteSpace(lqa)) { completed(false, true); yield break; }
            string artifactId = null;
            foreach (var artifact in envelope.artifacts ?? Array.Empty<InGameQaArtifact>())
            {
                var artifactPath = Path.Combine(QueueRoot, artifact.localId + ".png");
                if (!File.Exists(artifactPath)) { completed(false, false); yield break; }
                var create = "{\"filename\":\"" + Escape(envelope.eventId) + ".png\",\"contentType\":\"image/png\",\"kind\":\"lqa_screenshot\",\"format\":\"png\",\"byteSize\":" + artifact.byteSize + ",\"engine\":\"unity\",\"targetLocale\":\"" + Escape(envelope.locale) + "\",\"metadata\":{\"eventId\":\"" + Escape(envelope.eventId) + "\",\"sessionId\":\"" + Escape(envelope.sessionId) + "\",\"checksumSha256\":\"" + artifact.sha256 + "\"}}";
                string response = null; long status = 0;
                yield return Request(ProjectUrl("/artifacts/upload-sessions"), "POST", Encoding.UTF8.GetBytes(create), "application/json", "X-Connector-Token", connector, "ingame-" + envelope.eventId + ":artifact:" + artifact.localId, (body, code) => { response = body; status = code; });
                if (status < 200 || status >= 300 || !TryUploadSession(response, out artifactId, out var uploadUrl)) { completed(false, Retryable(status)); yield break; }
                yield return Request(uploadUrl, "PUT", File.ReadAllBytes(artifactPath), "image/png", "X-Connector-Token", connector, null, (_, code) => status = code);
                if (status < 200 || status >= 300) { completed(false, Retryable(status)); yield break; }
            }
            var issue = BuildIssueJson(envelope, artifactId); long issueStatus = 0;
            yield return Request(ProjectUrl("/lqa/issues"), "POST", Encoding.UTF8.GetBytes(issue), "application/json", "Authorization", "Bearer " + lqa, "ingame-" + envelope.eventId, (_, code) => issueStatus = code);
            completed(issueStatus >= 200 && issueStatus < 300, Retryable(issueStatus));
        }

        private static string Validate(InGameQaEnvelope value, byte[] screenshot)
        {
            if (value == null || value.schema != "breadtranslator.ingame-qa.v1") return "schema_invalid";
            if (value.consent == null || !value.consent.granted) return "consent_required";
            var categories = value.consent.categories ?? Array.Empty<string>();
            if (!categories.Contains("context") || !categories.Contains("telemetry")) return "consent_scope_invalid";
            if (screenshot != null && screenshot.Length > 0 && !categories.Contains("screenshot")) return "screenshot_consent_required";
            if (screenshot != null && screenshot.Length > MaxScreenshotBytes) return "artifact_size_exceeded";
            if (string.IsNullOrWhiteSpace(value.eventId) || string.IsNullOrWhiteSpace(value.sessionId) || value.issue == null || string.IsNullOrWhiteSpace(value.issue.title)) return "context_required";
            return null;
        }

        private IEnumerator Request(string url, string method, byte[] body, string contentType, string authHeader, string authValue, string idempotencyKey, Action<string, long> completed)
        {
            using (var request = new UnityWebRequest(url, method))
            {
                request.uploadHandler = new UploadHandlerRaw(body); request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 30;
                request.SetRequestHeader("Content-Type", contentType); request.SetRequestHeader(authHeader, authValue);
                if (!string.IsNullOrEmpty(idempotencyKey)) request.SetRequestHeader("Idempotency-Key", idempotencyKey);
                yield return request.SendWebRequest();
                completed(request.result == UnityWebRequest.Result.Success ? request.downloadHandler.text : null, request.responseCode);
            }
        }

        private string ProjectUrl(string suffix) => apiBaseUrl.TrimEnd('/') + "/api/workspaces/" + UnityWebRequest.EscapeURL(workspaceSlug) + "/projects/" + UnityWebRequest.EscapeURL(projectSlug) + suffix;
        private static bool Retryable(long status) => status == 0 || status == 408 || status == 429 || status >= 500;
        private static string BuildIssueJson(InGameQaEnvelope e, string artifactId) => "{\"title\":\"" + Escape(e.issue.title) + "\",\"description\":\"" + Escape(e.issue.description) + "\",\"category\":\"" + Escape(e.issue.category) + "\",\"severity\":\"" + Escape(e.issue.severity) + "\",\"targetLocale\":\"" + Escape(e.locale) + "\",\"buildId\":\"" + Escape(e.build?.id) + "\",\"platform\":\"" + Escape(e.platform) + "\",\"screenshotArtifactId\":\"" + Escape(artifactId) + "\",\"metadata\":{\"schema\":\"breadtranslator.ingame-qa.v1\",\"eventId\":\"" + Escape(e.eventId) + "\",\"sessionId\":\"" + Escape(e.sessionId) + "\",\"scene\":\"" + Escape(e.location?.scene) + "\",\"map\":\"" + Escape(e.location?.map) + "\",\"widget\":\"" + Escape(e.location?.widget) + "\"}}";
        private static bool TryUploadSession(string json, out string id, out string url) { id = Extract(json, "artifactId"); url = Extract(json, "uploadUrl"); return !string.IsNullOrEmpty(id) && Uri.TryCreate(url, UriKind.Absolute, out _); }
        private static string Extract(string json, string key) { if (string.IsNullOrEmpty(json)) return null; var marker = "\"" + key + "\":\""; var start = json.IndexOf(marker, StringComparison.Ordinal); if (start < 0) return null; start += marker.Length; var end = json.IndexOf('"', start); return end < 0 ? null : json.Substring(start, end - start).Replace("\\/", "/"); }
        private static string Escape(string value) => (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        private static string Redact(string value, int max) { var text = value ?? ""; foreach (var marker in SecretMarkers) if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) text = "[REDACTED]"; return text.Substring(0, Math.Min(text.Length, max)); }
        private static string Sha256(byte[] bytes) { using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2"))); }
        private static void AtomicWrite(string path, byte[] bytes) { var temp = path + ".tmp"; File.WriteAllBytes(temp, bytes); if (File.Exists(path)) File.Delete(path); File.Move(temp, path); }
        private static void DeleteQuietly(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        private static void Quarantine(string path) { try { File.Move(path, path + ".invalid"); } catch { DeleteQuietly(path); } }
    }
}
