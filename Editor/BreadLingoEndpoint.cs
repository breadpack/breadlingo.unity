using System;
using BreadTranslator.Unity;

namespace BreadTranslator.Unity.Editor
{
    internal static class BreadLingoEndpoint
    {
        internal static string Build(BreadTranslatorProjectSettings settings, string path)
        {
            var origin = Origin(settings);
            if (!path.StartsWith("/api/", StringComparison.Ordinal)) throw new ArgumentException("An API path is required.");
            return new Uri(origin, path).AbsoluteUri;
        }

        internal static void Validate(BreadTranslatorProjectSettings settings, string endpoint)
        {
            var origin = Origin(settings);
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
                !string.Equals(uri.GetLeftPart(UriPartial.Authority), origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Requests must use the configured HTTPS origin.");
        }

        private static Uri Origin(BreadTranslatorProjectSettings settings)
        {
            var text = string.IsNullOrWhiteSpace(settings?.ApiBaseUrl) ? "https://breadlingo.com" : settings.ApiBaseUrl.Trim();
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("API Base URL must be an HTTPS origin without a path, query or credentials.");
            return uri;
        }
    }
}
