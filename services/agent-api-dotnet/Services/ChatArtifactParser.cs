using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InfraAdvisor.AgentApi.Services;

/// <summary>
/// Validates MCP output before it becomes client-visible or persisted UI data.
/// Unknown versions, malformed envelopes, and payloads above 64 KiB fail closed.
/// </summary>
public static class ChatArtifactParser
{
    public const int MaxBytes = 64 * 1024;
    public const int MaxItems = 20;
    private const decimal MaxAmount = 1_000_000_000_000_000m;
    private static readonly IReadOnlyDictionary<string, string> Providers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sam.gov"] = "contract",
        ["grants.gov"] = "grant",
    };
    private static readonly HashSet<string> MissingFields = ["title", "agency.name", "deadline_at", "source.url", "funding.total"];
    private static readonly Regex DatePattern = new("^\\d{4}-\\d{2}-\\d{2}$", RegexOptions.CultureInvariant);
    private static readonly Regex UsDatePattern = new("^\\d{2}/\\d{2}/\\d{4}$", RegexOptions.CultureInvariant);
    private static readonly Regex DateTimePattern = new("^\\d{4}-\\d{2}-\\d{2}T.+(?:Z|[+-]\\d{2}:\\d{2})$", RegexOptions.CultureInvariant);
    private static readonly Regex NaicsPattern = new("^\\d{2,6}$", RegexOptions.CultureInvariant);
    private static readonly Regex AssistancePattern = new("^\\d{2}\\.\\d{3}$", RegexOptions.CultureInvariant);
    private static readonly Regex CurrencyPattern = new("^[A-Z]{3}$", RegexOptions.CultureInvariant);

    public static JsonElement? TryExtract(string raw, string? toolName, string? toolCallId)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(raw) > MaxBytes) return null;
        try
        {
            var root = JsonNode.Parse(raw);
            foreach (var candidate in CandidateObjects(root))
            {
                var artifact = TryValidate(candidate, toolName, toolCallId);
                if (artifact is not null) return artifact;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> ExtractSourceUrls(JsonElement artifact)
    {
        var sources = new List<string>();
        if (!artifact.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return sources;
        var isContractAwards = artifact.TryGetProperty("kind", out var kind) && kind.GetString() == "contract_awards";
        foreach (var item in items.EnumerateArray())
        {
            string? rawUrl = null;
            if (isContractAwards)
            {
                if (item.TryGetProperty("usaspending_permalink", out var permalink) && permalink.ValueKind == JsonValueKind.String)
                    rawUrl = permalink.GetString();
            }
            else if (item.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object &&
                     source.TryGetProperty("url", out var urlValue) && urlValue.ValueKind == JsonValueKind.String)
            {
                rawUrl = urlValue.GetString();
            }
            if (rawUrl is null || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                continue;
            var sanitized = new UriBuilder(url) { Query = "", Fragment = "" }.Uri.ToString();
            if (!sources.Contains(sanitized, StringComparer.Ordinal)) sources.Add(sanitized);
        }
        return sources;
    }

    private static IEnumerable<JsonObject> CandidateObjects(JsonNode? root)
    {
        if (root is JsonObject direct)
        {
            yield return direct;

            // McpClientTool serializes CallToolResult into FunctionResultContent.Result.
            // Only inspect protocol-defined result locations; never recursively search
            // provider payloads for artifact-looking objects.
            if (direct["structuredContent"] is JsonObject structured)
                yield return structured;
            if (direct["content"] is JsonArray content)
                foreach (var block in content.OfType<JsonObject>())
                    if (block["type"] is JsonValue type && type.TryGetValue<string>(out var typeName) && typeName == "text" && block["text"] is JsonValue text && text.TryGetValue<string>(out var encoded))
                    {
                        JsonNode? decoded = null;
                        try { decoded = JsonNode.Parse(encoded); }
                        catch (JsonException) { }
                        if (decoded is JsonObject decodedObject) yield return decodedObject;
                    }
        }
    }

    private static JsonElement? TryValidate(JsonObject candidate, string? toolName, string? toolCallId)
    {
        string? kindName;
        try
        {
            kindName = candidate["kind"]?.GetValue<string>();
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            return null;
        }
        return kindName switch
        {
            "procurement_opportunities" => TryValidateProcurement(candidate, toolName, toolCallId),
            "contract_awards" => TryValidateContractAwards(candidate, toolName, toolCallId),
            _ => null,
        };
    }

    private static JsonElement? TryValidateProcurement(JsonObject candidate, string? toolName, string? toolCallId)
    {
        try
        {
            using var doc = JsonDocument.Parse(candidate.ToJsonString());
            var root = doc.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "procurement_opportunities" ||
                !root.TryGetProperty("schema_version", out var version) || version.GetString() != "1.0" ||
                !root.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > MaxItems)
                return null;

            var status = ReadString(root, "status", 20);
            if (status is not ("ok" or "partial" or "empty" or "error")) return null;
            var normalizedItems = items.EnumerateArray().Select(NormalizeItem).ToList();
            if (!meta.TryGetProperty("returned_count", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var returnedCount) || returnedCount < 0 || returnedCount > MaxItems || returnedCount != normalizedItems.Count)
                return null;

            var providerCounts = NormalizeProviderCounts(meta, normalizedItems);
            if (!meta.TryGetProperty("truncated", out var truncated) || truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            var partialErrors = NormalizePartialErrors(meta);
            var selectedToolName = !string.IsNullOrWhiteSpace(toolName) ? ValidateString(toolName, 100) : ReadOptionalString(root, "tool_name", 100);
            var selectedToolCallId = toolCallId is null ? null : ValidateString(toolCallId, 200);

            var node = new JsonObject
            {
                ["kind"] = "procurement_opportunities",
                ["schema_version"] = "1.0",
                ["status"] = status,
                ["generated_at"] = ReadDateTime(root, "generated_at"),
                ["items"] = new JsonArray(normalizedItems.Cast<JsonNode?>().ToArray()),
                ["meta"] = new JsonObject
                {
                    ["returned_count"] = returnedCount,
                    ["provider_counts"] = providerCounts,
                    ["truncated"] = truncated.GetBoolean(),
                    ["partial_errors"] = partialErrors,
                },
                ["tool_call_id"] = selectedToolCallId,
            };
            if (selectedToolName is not null) node["tool_name"] = selectedToolName;
            var finalJson = node.ToJsonString();
            if (System.Text.Encoding.UTF8.GetByteCount(finalJson) > MaxBytes) return null;
            using var finalDoc = JsonDocument.Parse(finalJson);
            return finalDoc.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    private static JsonElement? TryValidateContractAwards(JsonObject candidate, string? toolName, string? toolCallId)
    {
        try
        {
            using var doc = JsonDocument.Parse(candidate.ToJsonString());
            var root = doc.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "contract_awards" ||
                !root.TryGetProperty("schema_version", out var version) || version.GetString() != "1.0" ||
                !root.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > MaxItems)
                return null;

            var status = ReadString(root, "status", 20);
            if (status is not ("ok" or "empty" or "error")) return null;

            // Dedup by award_id, first-seen-wins — defensive backstop even
            // though the tool itself now dedups too.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var normalizedItems = new List<JsonObject>();
            foreach (var rawItem in items.EnumerateArray())
            {
                var normalized = NormalizeContractAwardItem(rawItem);
                var awardId = normalized["award_id"]!.GetValue<string>();
                if (awardId.Length > 0 && !seen.Add(awardId)) continue;
                normalizedItems.Add(normalized);
            }

            if (!meta.TryGetProperty("truncated", out var truncated) || truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            var partialErrors = NormalizeContractAwardErrors(meta);
            var selectedToolName = !string.IsNullOrWhiteSpace(toolName) ? ValidateString(toolName, 100) : ReadOptionalString(root, "tool_name", 100);
            var selectedToolCallId = toolCallId is null ? null : ValidateString(toolCallId, 200);

            var node = new JsonObject
            {
                ["kind"] = "contract_awards",
                ["schema_version"] = "1.0",
                ["status"] = status,
                ["generated_at"] = ReadDateTime(root, "generated_at"),
                ["items"] = new JsonArray(normalizedItems.Cast<JsonNode?>().ToArray()),
                ["meta"] = new JsonObject
                {
                    ["returned_count"] = normalizedItems.Count,
                    ["truncated"] = truncated.GetBoolean(),
                    ["partial_errors"] = partialErrors,
                },
                ["tool_call_id"] = selectedToolCallId,
            };
            if (selectedToolName is not null) node["tool_name"] = selectedToolName;
            var finalJson = node.ToJsonString();
            if (System.Text.Encoding.UTF8.GetByteCount(finalJson) > MaxBytes) return null;
            using var finalDoc = JsonDocument.Parse(finalJson);
            return finalDoc.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    private static JsonObject NormalizeContractAwardItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new FormatException("item must be an object");
        var source = ReadObject(item, "source");
        if (ReadString(source, "name", 100) != "USASpending.gov") throw new FormatException("invalid contract award source");

        return new JsonObject
        {
            ["award_id"] = ReadString(item, "award_id", 200),
            ["recipient_name"] = ReadString(item, "recipient_name", 500),
            ["award_amount_usd"] = ReadNumberNode(item, "award_amount_usd"),
            ["awarding_agency"] = ReadString(item, "awarding_agency", 500),
            ["awarding_sub_agency"] = ReadString(item, "awarding_sub_agency", 500),
            ["description"] = ReadString(item, "description", 1000),
            ["place_of_performance"] = ReadString(item, "place_of_performance", 200),
            ["start_date"] = ReadDateOrDateTime(item, "start_date"),
            ["end_date"] = ReadDateOrDateTime(item, "end_date"),
            ["naics_description"] = ReadString(item, "naics_description", 300),
            ["contract_type"] = ReadString(item, "contract_type", 100),
            ["usaspending_permalink"] = ReadSanitizedUrl(item, "usaspending_permalink"),
            ["source"] = new JsonObject { ["name"] = "USASpending.gov", ["retrieved_at"] = ReadDateTime(source, "retrieved_at") },
        };
    }

    private static JsonArray NormalizeContractAwardErrors(JsonElement meta)
    {
        if (!meta.TryGetProperty("partial_errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() > 2)
            throw new FormatException("invalid partial errors");
        var result = new JsonArray();
        foreach (var error in errors.EnumerateArray())
        {
            if (error.ValueKind != JsonValueKind.Object) throw new FormatException("invalid partial error");
            if (!error.TryGetProperty("retriable", out var retriable) || retriable.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new FormatException("invalid partial error");
            result.Add(new JsonObject { ["code"] = ReadString(error, "code", 100), ["retriable"] = retriable.GetBoolean() });
        }
        return result;
    }

    private static JsonObject NormalizeItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new FormatException("item must be an object");
        var provider = ReadString(item, "provider", 20);
        var opportunityType = ReadString(item, "opportunity_type", 20);
        if (!Providers.TryGetValue(provider, out var requiredType) || opportunityType != requiredType)
            throw new FormatException("invalid provider or opportunity type");

        var agency = ReadObject(item, "agency");
        var location = ReadObject(item, "location");
        var classifications = ReadObject(item, "classifications");
        var funding = ReadObject(item, "funding");
        var source = ReadObject(item, "source");
        var dataQuality = ReadObject(item, "data_quality");
        var currency = ReadString(funding, "currency", 3);
        if (!CurrencyPattern.IsMatch(currency)) throw new FormatException("invalid currency");
        var minimum = ReadNumber(funding, "minimum");
        var maximum = ReadNumber(funding, "maximum");
        if (minimum is not null && maximum is not null && minimum > maximum) throw new FormatException("minimum exceeds maximum");
        var missingFields = ReadStringArray(dataQuality, "missing_fields", 20, 100);
        if (missingFields.Any(field => !MissingFields.Contains(field))) throw new FormatException("invalid missing field");

        return new JsonObject
        {
            ["id"] = ReadString(item, "id", 300),
            ["provider"] = provider,
            ["provider_id"] = ReadString(item, "provider_id", 200),
            ["opportunity_type"] = opportunityType,
            ["title"] = ReadString(item, "title", 500),
            ["agency"] = new JsonObject { ["name"] = ReadString(agency, "name", 500), ["code"] = ReadNullableString(agency, "code", 100) },
            ["summary"] = ReadString(item, "summary", 500),
            ["status"] = ReadString(item, "status", 100),
            ["posted_at"] = ReadDateOrDateTime(item, "posted_at"),
            ["deadline_at"] = ReadDateOrDateTime(item, "deadline_at"),
            ["location"] = new JsonObject
            {
                ["state_code"] = ReadNullableString(location, "state_code", 20),
                ["state_name"] = ReadNullableString(location, "state_name", 200),
                ["city"] = ReadNullableString(location, "city", 200),
            },
            ["classifications"] = new JsonObject
            {
                ["naics"] = ToJsonArray(ReadStringArray(classifications, "naics", 20, 6, NaicsPattern)),
                ["assistance_listing"] = ToJsonArray(ReadStringArray(classifications, "assistance_listing", 20, 6, AssistancePattern)),
                ["set_aside"] = ReadNullableString(classifications, "set_aside", 200),
            },
            ["funding"] = new JsonObject
            {
                ["currency"] = currency,
                ["minimum"] = minimum is null ? null : JsonValue.Create(minimum.Value),
                ["maximum"] = maximum is null ? null : JsonValue.Create(maximum.Value),
                ["total"] = ReadNumberNode(funding, "total"),
                ["expected_awards"] = ReadIntegerNode(funding, "expected_awards"),
            },
            ["source"] = new JsonObject { ["url"] = ReadSanitizedUrl(source, "url"), ["retrieved_at"] = ReadDateTime(source, "retrieved_at") },
            ["data_quality"] = new JsonObject { ["missing_fields"] = ToJsonArray(missingFields) },
        };
    }

    private static JsonObject NormalizeProviderCounts(JsonElement meta, IReadOnlyList<JsonObject> items)
    {
        var raw = ReadObject(meta, "provider_counts");
        var result = new JsonObject();
        var total = 0;
        foreach (var property in raw.EnumerateObject())
        {
            if (!Providers.ContainsKey(property.Name) || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var count) || count < 0 || count > MaxItems)
                throw new FormatException("invalid provider count");
            var actual = items.Count(item => item["provider"]?.GetValue<string>() == property.Name);
            if (count != actual) throw new FormatException("provider count does not match items");
            result[property.Name] = count;
            total += count;
        }
        if (total != items.Count) throw new FormatException("provider counts do not match item count");
        return result;
    }

    private static JsonArray NormalizePartialErrors(JsonElement meta)
    {
        if (!meta.TryGetProperty("partial_errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() > 2)
            throw new FormatException("invalid partial errors");
        var result = new JsonArray();
        foreach (var error in errors.EnumerateArray())
        {
            if (error.ValueKind != JsonValueKind.Object) throw new FormatException("invalid partial error");
            var provider = ReadString(error, "provider", 20);
            if (!Providers.ContainsKey(provider) || !error.TryGetProperty("retriable", out var retriable) || retriable.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new FormatException("invalid partial error");
            result.Add(new JsonObject { ["provider"] = provider, ["code"] = ReadString(error, "code", 100), ["retriable"] = retriable.GetBoolean() });
        }
        return result;
    }

    private static JsonElement ReadObject(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : throw new FormatException($"{name} must be an object");

    private static string ValidateString(string value, int maximum)
    {
        var normalized = value.Trim();
        if (normalized.Length > maximum) throw new FormatException("string exceeds bound");
        return normalized;
    }

    private static string ReadString(JsonElement parent, string name, int maximum) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? ValidateString(value.GetString()!, maximum) : throw new FormatException($"{name} must be a string");

    private static string? ReadOptionalString(JsonElement parent, string name, int maximum) =>
        parent.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? ValidateString(value.GetString()!, maximum) : throw new FormatException($"{name} must be a string") : null;

    private static string? ReadNullableString(JsonElement parent, string name, int maximum)
    {
        if (!parent.TryGetProperty(name, out var value)) throw new FormatException($"{name} is required");
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => ValidateString(value.GetString()!, maximum),
            _ => throw new FormatException($"{name} must be a string or null"),
        };
    }

    private static string ReadDateTime(JsonElement parent, string name)
    {
        var value = ReadString(parent, name, 50);
        if (!DateTimePattern.IsMatch(value) || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            throw new FormatException($"{name} must be an offset-aware ISO date-time");
        return value;
    }

    private static string? ReadDateOrDateTime(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element)) throw new FormatException($"{name} is required");
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw new FormatException($"{name} must be a string or null");
        var value = ValidateString(element.GetString()!, 50);
        if (DatePattern.IsMatch(value) && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return value;
        if (UsDatePattern.IsMatch(value) && DateOnly.TryParseExact(value, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var usDate)) return usDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (DateTimePattern.IsMatch(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) return value;
        throw new FormatException($"{name} must be an ISO date or offset-aware date-time");
    }

    private static decimal? ReadNumber(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) throw new FormatException($"{name} is required");
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) || number < 0 || number > MaxAmount)
            throw new FormatException($"{name} must be a bounded non-negative number or null");
        return number;
    }

    private static JsonNode? ReadNumberNode(JsonElement parent, string name)
    {
        var number = ReadNumber(parent, name);
        return number is null ? null : JsonValue.Create(number.Value);
    }

    private static JsonNode? ReadIntegerNode(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) throw new FormatException($"{name} is required");
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0 || number > 1_000_000)
            throw new FormatException($"{name} must be a bounded non-negative integer or null");
        return JsonValue.Create(number);
    }

    private static List<string> ReadStringArray(JsonElement parent, string name, int maximumItems, int maximumLength, Regex? pattern = null)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximumItems)
            throw new FormatException($"{name} must be a bounded array");
        var result = new List<string>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) throw new FormatException($"{name} entries must be strings");
            var normalized = ValidateString(entry.GetString()!, maximumLength);
            if (pattern is not null && !pattern.IsMatch(normalized)) throw new FormatException($"{name} contains an invalid value");
            result.Add(normalized);
        }
        return result;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static string? ReadSanitizedUrl(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) throw new FormatException($"{name} is required");
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new FormatException("invalid source URL");
        var sanitized = new UriBuilder(uri) { Query = "", Fragment = "" }.Uri.AbsoluteUri;
        if (sanitized.Length > 1_000) throw new FormatException("source URL exceeds bound");
        return sanitized;
    }
}
