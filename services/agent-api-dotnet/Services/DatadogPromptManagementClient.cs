using System.Text.Json;

namespace InfraAdvisor.AgentApi.Services;

// Thin HTTP client wrapping DD's (preview/"unstable") Prompt Registry API —
// same shape confirmed from ddtrace's own Python implementation
// (ddtrace/llmobs/_prompts/manager.py), since the public docs describe the
// stable v2 surface but the SDK itself still calls the unstable path:
//
//   GET https://api.<site>/api/unstable/llm-obs/v1/prompts/{prompt_id}
//   GET https://api.<site>/api/unstable/llm-obs/v1/prompts/{prompt_id}/versions/{version}
//
// The plain fetch is the static-registry "latest version" path — no
// DD_APPLICATION_KEY needed (that's only required for the env-scoped
// /resolve endpoint, which we still don't call directly here — instead a
// specific version can be pinned via PromptVersionFlags' Feature Flag,
// passed in as `version` below, hitting the versions/{version} path
// instead). Response is a flat JSON object with "template" (or
// "chat_template" for multi-message prompts — unused here, we only manage
// a single string system prompt) and "version"/"user_version".
//
// Disabled gracefully when DD_PROMPT_MANAGEMENT_ENABLED isn't "true" or
// DD_API_KEY isn't set, and fails OPEN (returns the caller's fallback) on
// any transport/parse/missing-field error — a Datadog outage or
// misconfiguration must never prevent the agent from starting up with a
// working system prompt. Mirrors DatadogAiGuardClient's fail-open shape.
public class DatadogPromptManagementClient
{
    private readonly HttpClient _http;
    private readonly ILogger<DatadogPromptManagementClient> _logger;
    private readonly string? _apiKey;
    private readonly string _site;
    private readonly bool _enabled;

    public DatadogPromptManagementClient(
        HttpClient http,
        ILogger<DatadogPromptManagementClient> logger)
    {
        _http = http;
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("DD_API_KEY");
        _site = Environment.GetEnvironmentVariable("DD_SITE") ?? "datadoghq.com";
        _enabled =
            string.Equals(Environment.GetEnvironmentVariable("DD_PROMPT_MANAGEMENT_ENABLED"), "true", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_apiKey);

        if (!_enabled)
            _logger.LogInformation(
                "DatadogPromptManagementClient disabled (DD_PROMPT_MANAGEMENT_ENABLED not \"true\" or DD_API_KEY unset) — using local fallback prompts.");
    }

    // Fetches promptId's registry version — pinned to `version` when
    // nonzero (a Feature Flags override, see PromptVersionFlags), otherwise
    // the latest — or falls back to `fallback` (the hardcoded local prompt)
    // on any failure. Never throws.
    public async Task<PromptFetchResult> GetPromptTemplateAsync(
        string promptId,
        string fallback,
        int version = 0,
        CancellationToken ct = default)
    {
        if (!_enabled)
            return new PromptFetchResult(fallback, "fallback", "fallback");

        try
        {
            var escapedId = Uri.EscapeDataString(promptId);
            var url = version > 0
                ? $"https://api.{_site}/api/unstable/llm-obs/v1/prompts/{escapedId}/versions/{version}"
                : $"https://api.{_site}/api/unstable/llm-obs/v1/prompts/{escapedId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("DD-API-KEY", _apiKey);

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Prompt Registry fetch for {PromptId} failed: HTTP {Status} — using local fallback.",
                    promptId, (int)resp.StatusCode);
                return new PromptFetchResult(fallback, "fallback", "fallback");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var template = root.TryGetProperty("template", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(template))
            {
                _logger.LogWarning(
                    "Prompt Registry response for {PromptId} had no usable \"template\" field — using local fallback.",
                    promptId);
                return new PromptFetchResult(fallback, "fallback", "fallback");
            }

            var resolvedVersion =
                (root.TryGetProperty("user_version", out var uv) ? uv.GetString() : null)
                ?? (root.TryGetProperty("version", out var v) ? v.ToString() : null)
                ?? "unknown";

            return new PromptFetchResult(template, resolvedVersion, version > 0 ? "flag-pinned" : "registry");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prompt Registry fetch for {PromptId} threw — using local fallback.", promptId);
            return new PromptFetchResult(fallback, "fallback", "fallback");
        }
    }
}

public record PromptFetchResult(string Template, string Version, string Source);
