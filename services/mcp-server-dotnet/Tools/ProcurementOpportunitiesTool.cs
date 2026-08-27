using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace InfraAdvisor.McpServer.Tools;

[McpServerToolType]
public sealed class ProcurementOpportunitiesTool(IHttpClientFactory httpFactory, ILogger<ProcurementOpportunitiesTool> logger)
{
    private const string SamGovApiUrl = "https://api.sam.gov/opportunities/v2/search";
    private const string GrantsGovSearchUrl = "https://api.grants.gov/v1/api/search2";
    private const double MaxFundingUsd = 1_000_000_000_000_000d;
    private static readonly Regex NaicsPattern = new("^\\d{2,6}$", RegexOptions.CultureInvariant);
    private static readonly Regex AssistancePattern = new("^\\d{2}\\.\\d{3}$", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, List<string>> NaicsMap = new()
    {
        ["water"] = new() { "237110" },
        ["sewer"] = new() { "237110" },
        ["bridge"] = new() { "237310" },
        ["highway"] = new() { "237310" },
        ["road"] = new() { "237310" },
        ["transportation"] = new() { "237310" },
        ["power"] = new() { "237130" },
        ["energy"] = new() { "237130" },
        ["pipeline"] = new() { "237120" },
        ["building"] = new() { "236220" },
        ["environmental"] = new() { "562910" },
        ["dam"] = new() { "237990" },
        ["flood"] = new() { "237990" },
    };

    private static readonly Dictionary<string, string> StateCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alabama"]="AL", ["Alaska"]="AK", ["Arizona"]="AZ", ["Arkansas"]="AR", ["California"]="CA", ["Colorado"]="CO", ["Connecticut"]="CT", ["Delaware"]="DE", ["District of Columbia"]="DC", ["Florida"]="FL", ["Georgia"]="GA", ["Hawaii"]="HI", ["Idaho"]="ID", ["Illinois"]="IL", ["Indiana"]="IN", ["Iowa"]="IA", ["Kansas"]="KS", ["Kentucky"]="KY", ["Louisiana"]="LA", ["Maine"]="ME", ["Maryland"]="MD", ["Massachusetts"]="MA", ["Michigan"]="MI", ["Minnesota"]="MN", ["Mississippi"]="MS", ["Missouri"]="MO", ["Montana"]="MT", ["Nebraska"]="NE", ["Nevada"]="NV", ["New Hampshire"]="NH", ["New Jersey"]="NJ", ["New Mexico"]="NM", ["New York"]="NY", ["North Carolina"]="NC", ["North Dakota"]="ND", ["Ohio"]="OH", ["Oklahoma"]="OK", ["Oregon"]="OR", ["Pennsylvania"]="PA", ["Rhode Island"]="RI", ["South Carolina"]="SC", ["South Dakota"]="SD", ["Tennessee"]="TN", ["Texas"]="TX", ["Utah"]="UT", ["Vermont"]="VT", ["Virginia"]="VA", ["Washington"]="WA", ["West Virginia"]="WV", ["Wisconsin"]="WI", ["Wyoming"]="WY"
    };

    [McpServerTool(Name = "get_procurement_opportunities")]
    [Description(
        "SAM.gov + grants.gov — ACTIVE / OPEN federal opportunities (contracts and " +
        "grants). Returns the versioned procurement_opportunities chat artifact, " +
        "sorted by deadline. Requires SAMGOV_API_KEY env var for contract results.\n" +
        "Coverage: every currently-open federal solicitation + open grant program.\n" +
        "Use when the user asks: open RFPs for <work type>; upcoming bid deadlines; " +
        "active federal grant programs; what's on SAM.gov right now for <NAICS>; " +
        "opportunities matching firm capabilities.\n" +
        "PAIRING RULE: Call get_contract_awards FIRST when the user is doing BD " +
        "research — knowing past winners + pricing informs which open opportunities " +
        "are worth pursuing.\n" +
        "Do NOT use for: HISTORICAL awards (use get_contract_awards); state / local " +
        "RFPs (use search_web_procurement); web search beyond .gov.\n" +
        "Date range: tool internally defaults to the next 90 days — NEVER ask the " +
        "user for a date range. Tool tolerates SAM.gov rate-limit errors (429) and " +
        "returns retriable:true.")]
    public async Task<string> GetProcurementOpportunitiesAsync(
        [Description("Natural-language search query — e.g. 'civil engineering', 'bridge inspection', 'water treatment'.")] string query,
        [Description("State 2-letter abbreviation 'TX' or 'state + city'. Omit for nationwide.")] string? geography = null,
        [Description("NAICS codes. AEC examples: ['237310'] highway, ['237110'] water/sewer, ['237990'] heavy civil, ['541330'] engineering services.")] List<string>? naics_codes = null,
        [Description("Minimum contract / grant value in USD. Use 1000000 for major-only filtering.")] int? min_value_usd = null,
        [Description("Maximum contract / grant value in USD.")] int? max_value_usd = null,
        [Description("Filter source: ['contract'] for SAM.gov only, ['grant'] for grants.gov only. Omit for merged results from both.")] List<string>? opportunity_types = null,
        [Description("Max results (1-100). Default 20.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var toolStarted = Stopwatch.GetTimestamp();
        var derivedNaics = naics_codes ?? DeriveNaics(query);
        var opTypes = opportunity_types ?? new List<string> { "contract", "grant" };
        var includeContracts = opTypes.Contains("contract");
        var includeGrants = opTypes.Contains("grant");

        var samTask = includeContracts
            ? FetchSamGov(query, geography, derivedNaics, limit, cancellationToken)
            : Task.FromResult<object>(new List<Dictionary<string, object?>>());

        var grantsTask = includeGrants
            ? FetchGrantsGov(query, limit, cancellationToken)
            : Task.FromResult<object>(new List<Dictionary<string, object?>>());

        await Task.WhenAll(samTask, grantsTask);

        var samResult = samTask.Result;
        var grantsResult = grantsTask.Result;
        var grantsItems = grantsResult as List<Dictionary<string, object?>> ?? [];
        var grantsError = grantsResult as Dictionary<string, object?>;

        List<Dictionary<string, object?>> samItems = new();
        Dictionary<string, object?>? samError = null;

        if (samResult is List<Dictionary<string, object?>> list)
        {
            samItems = list;
        }
        else if (samResult is Dictionary<string, object?> dict)
        {
            if (dict.ContainsKey("error"))
                samError = dict;
            else if (dict.TryGetValue("results", out var r) && r is List<Dictionary<string, object?>> rl)
                samItems = rl;
        }

        var allResults = samItems.Concat(grantsItems)
            .Select(NormalizeArtifactItem)
            .Where(item => item is not null)
            .Cast<Dictionary<string, object?>>()
            .Where(item => IsWithinValueRange(item, min_value_usd, max_value_usd))
            .ToList();

        // Sort by deadline
        allResults.Sort((a, b) =>
        {
            var da = GetDeadlineKey(a);
            var db = GetDeadlineKey(b);
            return string.Compare(da, db, StringComparison.Ordinal);
        });

        var bounded = allResults.Take(Math.Clamp(limit, 1, 20)).ToList();
        var errors = new List<object>();
        if (samError is not null) errors.Add(new { provider = "sam.gov", code = SafeErrorCode(samError), retriable = samError.GetValueOrDefault("retriable") as bool? ?? false });
        if (grantsError is not null) errors.Add(new { provider = "grants.gov", code = SafeErrorCode(grantsError), retriable = grantsError.GetValueOrDefault("retriable") as bool? ?? false });
        var providerCounts = new Dictionary<string, int>
        {
            ["sam.gov"] = bounded.Count(item => Equals(item.GetValueOrDefault("provider"), "sam.gov")),
            ["grants.gov"] = bounded.Count(item => Equals(item.GetValueOrDefault("provider"), "grants.gov")),
        };
        var artifact = new
        {
            kind = "procurement_opportunities", schema_version = "1.0", tool_name = "get_procurement_opportunities", tool_call_id = (string?)null,
            status = errors.Count > 0 ? (bounded.Count > 0 ? "partial" : "error") : bounded.Count == 0 ? "empty" : "ok",
            generated_at = DateTimeOffset.UtcNow.ToString("O"), items = bounded,
            meta = new { returned_count = bounded.Count, provider_counts = providerCounts, truncated = allResults.Count > bounded.Count, partial_errors = errors }
        };
        var sample = bounded.Take(3).Select(i => new
        {
            id = i["id"],
            provider = i["provider"],
            opportunity_type = i["opportunity_type"],
            status = i["status"],
            state_code = GetNestedProperty(i, "location", "state_code"),
            deadline_at = i["deadline_at"],
            funding_total = GetNestedProperty(i, "funding", "total"),
            missing_fields = GetNestedProperty(i, "data_quality", "missing_fields"),
        }).ToArray();
        logger.LogInformation(
            "Normalized procurement artifact {Event} {ToolName} {ArtifactKind} {ArtifactSchemaVersion} {ArtifactStatus} {ArtifactReturnedCount} {@ArtifactProviderCounts} {ArtifactTruncated} {ArtifactPartialErrorCount} {@ArtifactSample} {DurationMs}",
            "procurement.artifact.normalized",
            "get_procurement_opportunities",
            "procurement_opportunities",
            "1.0",
            artifact.status,
            artifact.meta.returned_count,
            artifact.meta.provider_counts,
            artifact.meta.truncated,
            errors.Count,
            sample,
            Math.Round(Stopwatch.GetElapsedTime(toolStarted).TotalMilliseconds, 2));
        return JsonSerializer.Serialize(artifact);
    }

    private static JsonElement? GetNestedProperty(Dictionary<string, object?> item, string containerName, string propertyName)
    {
        if (!item.TryGetValue(containerName, out var container) || container is null)
            return null;
        var element = JsonSerializer.SerializeToElement(container);
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property)
            ? property.Clone()
            : null;
    }

    private static Dictionary<string, object?>? NormalizeArtifactItem(Dictionary<string, object?> candidate)
    {
        var root = JsonSerializer.SerializeToElement(candidate);
        var provider = StringValue(root, "provider", 20);
        if (provider is not ("sam.gov" or "grants.gov")) return null;

        var providerId = StringValue(root, "provider_id", 200);
        var title = StringValue(root, "title", 500);
        var agencyName = NestedStringValue(root, "agency", "name", 500);
        var deadline = DateValue(root, "deadline_at");
        var sourceUrl = SanitizeUrl(NestedStringValue(root, "source", "url", 1000));
        var retrievedAt = NestedDateTimeValue(root, "source", "retrieved_at") ?? DateTimeOffset.UtcNow.ToString("O");
        var fundingTotal = NestedNumberValue(root, "funding", "total");
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(title)) missing.Add("title");
        if (string.IsNullOrWhiteSpace(agencyName)) missing.Add("agency.name");
        if (deadline is null) missing.Add("deadline_at");
        if (sourceUrl is null) missing.Add("source.url");
        if (fundingTotal is null) missing.Add("funding.total");

        return new Dictionary<string, object?>
        {
            ["id"] = TruncateString($"{provider}:{providerId}", 300),
            ["provider"] = provider,
            ["provider_id"] = providerId,
            ["opportunity_type"] = provider == "sam.gov" ? "contract" : "grant",
            ["title"] = title,
            ["agency"] = new { name = agencyName, code = NullIfEmpty(NestedStringValue(root, "agency", "code", 100)) },
            ["summary"] = StringValue(root, "summary", 500),
            ["status"] = StringValue(root, "status", 100),
            ["posted_at"] = DateValue(root, "posted_at"),
            ["deadline_at"] = deadline,
            ["location"] = new
            {
                state_code = NullIfEmpty(NestedStringValue(root, "location", "state_code", 20)),
                state_name = NullIfEmpty(NestedStringValue(root, "location", "state_name", 200)),
                city = NullIfEmpty(NestedStringValue(root, "location", "city", 200)),
            },
            ["classifications"] = new
            {
                naics = NestedCodeList(root, "classifications", "naics", NaicsPattern),
                assistance_listing = NestedCodeList(root, "classifications", "assistance_listing", AssistancePattern),
                set_aside = NullIfEmpty(NestedStringValue(root, "classifications", "set_aside", 200)),
            },
            ["funding"] = new
            {
                currency = "USD",
                minimum = NestedNumberValue(root, "funding", "minimum"),
                maximum = NestedNumberValue(root, "funding", "maximum"),
                total = fundingTotal,
                expected_awards = NestedIntegerValue(root, "funding", "expected_awards", 1_000_000),
            },
            ["source"] = new { url = sourceUrl, retrieved_at = retrievedAt },
            ["data_quality"] = new { missing_fields = missing.ToArray() },
        };
    }

    private static bool IsWithinValueRange(Dictionary<string, object?> item, int? minimum, int? maximum)
    {
        if (minimum is null && maximum is null) return true;
        var root = JsonSerializer.SerializeToElement(item);
        var value = NestedNumberValue(root, "funding", "total") ?? NestedNumberValue(root, "funding", "maximum") ?? NestedNumberValue(root, "funding", "minimum");
        return value is not null && (minimum is null || value >= minimum) && (maximum is null || value <= maximum);
    }

    private static string StringValue(JsonElement element, string key, int limit) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? TruncateString(value.GetString()?.Trim() ?? "", limit)
            : "";

    private static string NestedStringValue(JsonElement element, string container, string key, int limit) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(container, out var nested)
            ? StringValue(nested, key, limit)
            : "";

    private static string? DateValue(JsonElement element, string key)
    {
        var value = StringValue(element, key, 50);
        return IsValidDate(value) ? value : null;
    }

    private static string? NestedDateTimeValue(JsonElement element, string container, string key)
    {
        var value = NestedStringValue(element, container, key, 50);
        return value.Contains('T') && IsValidDate(value) ? value : null;
    }

    private static bool IsValidDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    private static double? NestedNumberValue(JsonElement element, string container, string key)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(container, out var nested) || nested.ValueKind != JsonValueKind.Object || !nested.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)) return null;
        return double.IsFinite(number) && number >= 0 && number <= MaxFundingUsd ? number : null;
    }

    private static int? NestedIntegerValue(JsonElement element, string container, string key, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(container, out var nested) || nested.ValueKind != JsonValueKind.Object || !nested.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) return null;
        return number is >= 0 && number <= maximum ? number : null;
    }

    private static string[] NestedCodeList(JsonElement element, string container, string key, Regex pattern)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(container, out var nested) || nested.ValueKind != JsonValueKind.Object || !nested.TryGetProperty(key, out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()?.Trim() ?? "")
            .Where(value => pattern.IsMatch(value))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray();
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private async Task<object> FetchSamGov(string query, string? geography, List<string> naicsCodes, int limit, CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("SAMGOV_API_KEY") ?? "";
        if (string.IsNullOrEmpty(apiKey))
            return new Dictionary<string, object?> { ["error"] = "SAMGOV_API_KEY not configured", ["retriable"] = false };

        var (postedFrom, postedTo, _) = BuildDateRange(364);

        var paramPairs = new List<(string, string)>
        {
            ("limit", Math.Clamp(limit, 1, 20).ToString()),
            ("offset", "0"),
            ("ptype", "o"),
            ("ptype", "p"),
            ("ptype", "k"),
            ("ptype", "r"),
            ("postedFrom", postedFrom),
            ("postedTo", postedTo),
            ("api_key", apiKey),
        };

        foreach (var code in naicsCodes) paramPairs.Add(("ncode", code));
        var stateCode = NormalizeState(geography);
        if (stateCode is not null) paramPairs.Add(("state", stateCode));

        var qs = string.Join("&", paramPairs.Select(p => $"{Uri.EscapeDataString(p.Item1)}={Uri.EscapeDataString(p.Item2)}"));
        var url = $"{SamGovApiUrl}?{qs}";

        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        HttpResponseMessage resp;
        try
        {
            resp = await client.GetAsync(url, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning("SAM.gov request failed: {ErrorType}", ex.GetType().Name);
            return new Dictionary<string, object?> { ["error"] = "request_failed", ["source"] = "samgov", ["retriable"] = true };
        }

        var statusCode = (int)resp.StatusCode;

        if (statusCode == 400)
        {
            var errBody = await resp.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                using var errDoc = JsonDocument.Parse(errBody);
                var errMsg = errDoc.RootElement.TryGetProperty("errorMessage", out var em) ? em.GetString() :
                             errDoc.RootElement.TryGetProperty("errorCode", out var ec) ? ec.ToString() : errBody;
                if (errMsg?.Contains("Date range") == true)
                    return new Dictionary<string, object?> { ["error"] = $"SAM.gov rejected the request: date range must be within 1 year. Raw message: {errMsg}", ["source"] = "samgov", ["retriable"] = false };
                return new Dictionary<string, object?> { ["error"] = $"SAM.gov API error 400: {errMsg}", ["source"] = "samgov", ["retriable"] = false };
            }
            catch
            {
                return new Dictionary<string, object?> { ["error"] = $"SAM.gov API error 400: {errBody}", ["source"] = "samgov", ["retriable"] = false };
            }
        }

        if (statusCode == 403)
            return new Dictionary<string, object?> { ["error"] = "SAM.gov API returned 403 — API key may need up to 24 hours to activate after registration at api.sam.gov", ["source"] = "samgov", ["retriable"] = false };

        if (statusCode >= 400)
            return new Dictionary<string, object?> { ["error"] = $"SAM.gov API error: HTTP {statusCode}", ["source"] = "samgov", ["retriable"] = statusCode >= 500 };

        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var body = doc.RootElement;

        if (!body.TryGetProperty("opportunitiesData", out var oppsElem))
        {
            logger.LogWarning("SAM.gov response missing 'opportunitiesData' key");
            return new Dictionary<string, object?>
            {
                ["error"] = "SAM.gov response format unexpected — 'opportunitiesData' key missing",
                ["source"] = "samgov",
                ["retriable"] = false,
                ["response_keys"] = body.EnumerateObject().Select(p => p.Name).ToList(),
            };
        }

        var opps = oppsElem.EnumerateArray().ToList();
        if (opps.Count == 0)
        {
            return new Dictionary<string, object?>
            {
                ["results"] = new List<object>(),
                ["_note"] = $"No results found. NAICS codes queried: {string.Join(", ", naicsCodes)}",
            };
        }

        var retrievedAt = DateTimeOffset.UtcNow.ToString("O");
        return opps.Select(opp =>
        {
            var providerId = GetStr(opp, "noticeId") ?? GetStr(opp, "solicitationNumber") ?? "";
            var title = GetStr(opp, "title") ?? "";
            var agencyName = GetStr(opp, "fullParentPathName") ?? GetStr(opp, "organizationName") ?? "";
            var deadline = GetStr(opp, "responseDeadLine") ?? GetStr(opp, "archiveDate");
            var sourceUrl = SanitizeUrl(GetStr(opp, "uiLink") ?? GetFirstArrayString(opp, "resourceLinks"));
            var stateCode = GetNestedStr(opp, "placeOfPerformance", "state", "code") ?? GetNestedStr(opp, "placeOfPerformance", "stateCode");
            var stateName = GetNestedStr(opp, "placeOfPerformance", "state", "name") ?? GetNestedStr(opp, "placeOfPerformance", "stateName");
            var city = GetNestedStr(opp, "placeOfPerformance", "city", "name") ?? GetNestedStr(opp, "placeOfPerformance", "city");
            var missingFields = MissingFields(("title", title), ("agency.name", agencyName), ("deadline_at", deadline), ("source.url", sourceUrl));

            return new Dictionary<string, object?>
            {
                ["id"] = $"sam.gov:{providerId}", ["provider"] = "sam.gov", ["provider_id"] = providerId, ["opportunity_type"] = "contract",
                ["title"] = title, ["agency"] = new { name = agencyName, code = (string?)null },
                // SAM.gov's description is commonly an API link. Avoid storing
                // provider bodies or contact blocks in the conversation.
                ["summary"] = "", ["status"] = (GetStr(opp, "type") ?? "unknown").ToLowerInvariant(),
                ["posted_at"] = GetStr(opp, "postedDate"), ["deadline_at"] = deadline,
                ["location"] = new { state_code = stateCode, state_name = stateName, city },
                ["classifications"] = new { naics = GetStr(opp, "naicsCode") is string n ? new[] { n } : Array.Empty<string>(), assistance_listing = Array.Empty<string>(), set_aside = GetStr(opp, "typeOfSetAsideDescription") },
                ["funding"] = new { currency = "USD", minimum = (double?)null, maximum = (double?)null, total = GetNestedNumber(opp, "award", "amount"), expected_awards = (int?)null },
                ["source"] = new { url = sourceUrl, retrieved_at = retrievedAt }, ["data_quality"] = new { missing_fields = missingFields },
            };
        }).ToList();
    }

    private async Task<object> FetchGrantsGov(string query, int limit, CancellationToken cancellationToken)
    {
        var payload = new { keyword = query, oppStatuses = "forecasted|posted", rows = Math.Clamp(limit, 1, 20) };

        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);

        try
        {
            var resp = await client.PostAsJsonAsync(GrantsGovSearchUrl, payload, cancellationToken);
            if ((int)resp.StatusCode >= 400)
            {
                logger.LogWarning("grants.gov API returned {StatusCode}", resp.StatusCode);
                return new Dictionary<string, object?> { ["error"] = $"HTTP {(int)resp.StatusCode}", ["retriable"] = (int)resp.StatusCode >= 500 };
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var body = doc.RootElement;

            List<JsonElement> rawOpps;
            if (body.TryGetProperty("data", out var d) && d.TryGetProperty("oppHits", out var hits))
                rawOpps = hits.EnumerateArray().ToList();
            else
                rawOpps = new List<JsonElement>();

            var results = new List<Dictionary<string, object?>>();
            foreach (var opp in rawOpps)
            {
                var id = GetStr(opp, "id") ?? GetStr(opp, "number") ?? "";
                var listings = opp.TryGetProperty("alnist", out var al) && al.ValueKind == JsonValueKind.Array ? al.EnumerateArray().Select(x => x.ToString()).ToArray() : Array.Empty<string>();
                var title = GetStr(opp, "title") ?? "";
                var agencyName = GetStr(opp, "agencyName") ?? "";
                var deadline = GetStr(opp, "closeDate");
                var sourceUrl = SanitizeUrl($"https://www.grants.gov/search-results-detail/{Uri.EscapeDataString(id)}");
                results.Add(new Dictionary<string, object?>
                {
                    ["id"] = $"grants.gov:{id}", ["provider"] = "grants.gov", ["provider_id"] = id, ["opportunity_type"] = "grant", ["title"] = title,
                    ["agency"] = new { name = agencyName, code = GetStr(opp, "agencyCode") }, ["summary"] = TruncateString(GetStr(opp, "description") ?? "", 500), ["status"] = (GetStr(opp, "oppStatus") ?? "unknown").ToLowerInvariant(),
                    ["posted_at"] = GetStr(opp, "openDate"), ["deadline_at"] = deadline, ["location"] = new { state_code = (string?)null, state_name = (string?)null, city = (string?)null },
                    ["classifications"] = new { naics = Array.Empty<string>(), assistance_listing = listings, set_aside = (string?)null }, ["funding"] = new { currency = "USD", minimum = (double?)null, maximum = (double?)null, total = GetNumber(opp, "estimatedTotalProgramFunding"), expected_awards = GetNumber(opp, "expectedNumberOfAwards") },
                    ["source"] = new { url = sourceUrl, retrieved_at = DateTimeOffset.UtcNow.ToString("O") }, ["data_quality"] = new { missing_fields = MissingFields(("title", title), ("agency.name", agencyName), ("deadline_at", deadline)) },
                });
            }

            return results;
        }
        catch (Exception ex)
        {
            logger.LogWarning("grants.gov fetch failed: {ErrorType}", ex.GetType().Name);
            return new Dictionary<string, object?> { ["error"] = "request_failed", ["retriable"] = true };
        }
    }

    private static List<string> DeriveNaics(string query)
    {
        var q = query.ToLowerInvariant();
        var codes = new List<string>();
        var seen = new HashSet<string>();
        foreach (var (term, termCodes) in NaicsMap)
        {
            if (q.Contains(term))
                foreach (var c in termCodes)
                    if (seen.Add(c)) codes.Add(c);
        }
        if (codes.Count == 0)
        {
            var allCodes = NaicsMap.Values.SelectMany(x => x).Distinct().ToList();
            codes.AddRange(allCodes);
        }
        return codes;
    }

    private static string? NormalizeState(string? geography)
    {
        if (string.IsNullOrWhiteSpace(geography)) return null;
        var value = geography.Trim();
        if (value.Length == 2 && value.All(char.IsLetter)) return value.ToUpperInvariant();
        if (StateCodes.TryGetValue(value, out var exact)) return exact;
        return StateCodes.FirstOrDefault(pair => value.Contains(pair.Key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static (string from, string to, bool clamped) BuildDateRange(int daysBack)
    {
        var today = DateTime.UtcNow.Date;
        var from = today.AddDays(-daysBack);
        var to = today;
        var clamped = false;
        if ((to - from).Days > 365)
        {
            to = from.AddDays(365);
            clamped = true;
        }
        return (from.ToString("MM/dd/yyyy"), to.ToString("MM/dd/yyyy"), clamped);
    }

    private static string GetDeadlineKey(Dictionary<string, object?> item)
    {
        return item.TryGetValue("deadline_at", out var d) && d is string s && !string.IsNullOrEmpty(s) ? s : "9999";
    }

    private static string? GetStr(JsonElement elem, string key)
    {
        if (!elem.TryGetProperty(key, out var val)) return null;
        return val.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => val.GetString(), _ => val.ToString() };
    }

    private static string? GetNestedStr(JsonElement element, params string[] path)
    {
        foreach (var key in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out element)) return null;
        }
        return element.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => element.GetString(), _ => element.ToString() };
    }

    private static string? GetFirstArrayString(JsonElement element, string key) =>
        element.TryGetProperty(key, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : null;

    private static double? GetNumber(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private static double? GetNestedNumber(JsonElement element, params string[] path)
    {
        foreach (var key in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out element)) return null;
        }
        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number) ? number : null;
    }

    private static string[] MissingFields(params (string Name, string? Value)[] fields) =>
        fields.Where(field => string.IsNullOrWhiteSpace(field.Value)).Select(field => field.Name).ToArray();

    private static string? SanitizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
        var safe = new UriBuilder(uri) { Query = "", Fragment = "", UserName = "", Password = "" }.Uri.ToString();
        return safe.Length <= 1000 ? safe : null;
    }

    private static string TruncateString(string s, int maxLen) =>
        s.Length > maxLen ? s[..maxLen] : s;

    private static string SafeErrorCode(Dictionary<string, object?> error)
    {
        var message = error.GetValueOrDefault("error")?.ToString()?.ToLowerInvariant() ?? "";
        if (message.Contains("not configured")) return "not_configured";
        if (message.Contains("403")) return "forbidden";
        if (message.Contains("400") || message.Contains("date range")) return "invalid_request";
        if (message.Contains("format unexpected")) return "unexpected_response";
        var httpStatus = Regex.Match(message, @"\bhttp\s+(\d{3})\b", RegexOptions.CultureInvariant);
        if (httpStatus.Success) return $"http_{httpStatus.Groups[1].Value}";
        return "request_failed";
    }
}
