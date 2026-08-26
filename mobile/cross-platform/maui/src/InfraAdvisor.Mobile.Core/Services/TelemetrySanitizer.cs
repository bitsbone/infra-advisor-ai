using System.Text.RegularExpressions;

namespace InfraAdvisor.Mobile.Services;

/// <summary>
/// Enforces the public demo's custom-telemetry boundary before values reach Datadog. Network resource URLs retain only scheme, host, port, and path; application attributes with sensitive key names are discarded.
/// </summary>
public static partial class TelemetrySanitizer
{
    private static readonly string[] SensitiveKeyFragments =
    [
        "authorization", "credential", "email", "filename", "local_path", "password", "payload", "prompt", "query_text", "response_body", "sas", "token", "upload_url", "url",
    ];

    public static string SanitizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return StripQueryAndFragment(value);
        }

        return uri.GetLeftPart(UriPartial.Path);
    }

    public static Dictionary<string, object> FilterAttributes(IReadOnlyDictionary<string, object>? attributes)
    {
        var safe = new Dictionary<string, object>(StringComparer.Ordinal);
        if (attributes is null)
        {
            return safe;
        }

        foreach (var (key, value) in attributes)
        {
            var normalized = key.Replace('-', '_').Replace('.', '_').ToLowerInvariant();
            if (SensitiveKeyFragments.Any(normalized.Contains))
            {
                continue;
            }

            safe[key] = value;
        }

        return safe;
    }

    public static string SanitizeDiagnosticText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return AbsoluteUrlPattern().Replace(value, match => SanitizeUrl(match.Value));
    }

    public static string SanitizeActionName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "interaction";
        }

        return value.Length > 80 || value.Contains('?') || value.Contains('@') || value.Contains("http", StringComparison.OrdinalIgnoreCase)
            ? "redacted input action"
            : value;
    }

    private static string StripQueryAndFragment(string value)
    {
        var boundary = value.IndexOfAny(['?', '#']);
        return boundary < 0 ? value : value[..boundary];
    }

    [GeneratedRegex(@"https?://[^\s\]\[()<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteUrlPattern();
}
