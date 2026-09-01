using System.Text.Json;
using System.Text.Json.Nodes;
using InfraAdvisor.AgentApi.Services;
using InfraAdvisor.AgentApi.Models;
using Xunit;

namespace InfraAdvisor.AgentApi.Tests;

public sealed class ChatArtifactParserTests
{
    private const string ValidArtifact = """
        {"kind":"procurement_opportunities","schema_version":"1.0","status":"ok","generated_at":"2026-01-15T12:00:00Z","items":[],"meta":{"returned_count":0,"provider_counts":{},"truncated":false,"partial_errors":[]}}
        """;

    private const string ValidArtifactWithItem = """
        {"kind":"procurement_opportunities","schema_version":"1.0","status":"ok","generated_at":"2026-01-15T12:00:00Z","items":[{"id":"sam.gov:SAMPLE-1","provider":"sam.gov","provider_id":"SAMPLE-1","opportunity_type":"contract","title":"Resilience assessment","agency":{"name":"Example Agency","code":null},"summary":"Sanitized summary","status":"posted","posted_at":"01/10/2026","deadline_at":"2026-02-28","location":{"state_code":"TX","state_name":"Texas","city":null},"classifications":{"naics":["541330"],"assistance_listing":[],"set_aside":null},"funding":{"currency":"USD","minimum":1000,"maximum":2000,"total":1500,"expected_awards":1},"source":{"url":"https://sam.gov/opportunities/example?api_key=secret#details","retrieved_at":"2026-01-15T12:00:00Z"},"data_quality":{"missing_fields":[]}}],"meta":{"returned_count":1,"provider_counts":{"sam.gov":1},"truncated":false,"partial_errors":[]}}
        """;

    [Fact]
    public void ValidArtifactAddsToolCorrelation()
    {
        var result = ChatArtifactParser.TryExtract(ValidArtifact, "get_procurement_opportunities", "call-42");

        Assert.NotNull(result);
        Assert.Equal("get_procurement_opportunities", result.Value.GetProperty("tool_name").GetString());
        Assert.Equal("call-42", result.Value.GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public void UnknownVersionAndOversizedFinalPayloadFailClosed()
    {
        Assert.Null(ChatArtifactParser.TryExtract(ValidArtifact.Replace("\"1.0\"", "\"2.0\""), null, null));
        var oversized = JsonSerializer.Serialize(new
        {
            kind = "procurement_opportunities", schema_version = "1.0", status = "ok", generated_at = "2026-01-15T12:00:00Z",
            items = new[] { new { summary = new string('x', ChatArtifactParser.MaxBytes) } }, meta = new { }
        });
        Assert.Null(ChatArtifactParser.TryExtract(oversized, "tool", "call"));
    }

    [Fact]
    public void NonStreamAndSseModelsPreserveTheCanonicalEnvelope()
    {
        var artifact = ChatArtifactParser.TryExtract(ValidArtifact, "get_procurement_opportunities", "call-42")!.Value;
        var responseJson = JsonSerializer.Serialize(new QueryResponse("answer", [], null, null, "session", "model", [artifact]));
        var eventJson = JsonSerializer.Serialize(new ArtifactEvent(artifact), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        using var response = JsonDocument.Parse(responseJson);
        using var streamEvent = JsonDocument.Parse(eventJson);
        Assert.Equal("procurement_opportunities", response.RootElement.GetProperty("artifacts")[0].GetProperty("kind").GetString());
        Assert.Equal("procurement_opportunities", streamEvent.RootElement.GetProperty("artifact").GetProperty("kind").GetString());
        Assert.False(streamEvent.RootElement.GetProperty("artifact").TryGetProperty("value", out _));
    }

    [Fact]
    public void McpCallToolResultTextAndStructuredContentAreUnwrapped()
    {
        using var artifactDocument = JsonDocument.Parse(ValidArtifact);
        var artifact = artifactDocument.RootElement.Clone();
        var textEnvelope = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = ValidArtifact } },
            isError = false,
        });
        var structuredEnvelope = JsonSerializer.Serialize(new { content = Array.Empty<object>(), structuredContent = artifact, isError = false });

        Assert.Equal("text-call", ChatArtifactParser.TryExtract(textEnvelope, "tool", "text-call")!.Value.GetProperty("tool_call_id").GetString());
        Assert.Equal("structured-call", ChatArtifactParser.TryExtract(structuredEnvelope, "tool", "structured-call")!.Value.GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public void ArbitraryNestedProviderJsonIsNotPromoted()
    {
        using var artifactDocument = JsonDocument.Parse(ValidArtifact);
        var nested = JsonSerializer.Serialize(new { provider_response = new { result = artifactDocument.RootElement.Clone() } });

        Assert.Null(ChatArtifactParser.TryExtract(nested, "tool", "call"));
    }

    [Fact]
    public void SourceUrlsAreSanitizedBeforeBecomingCitations()
    {
        var artifact = ChatArtifactParser.TryExtract(ValidArtifactWithItem, "tool", "call")!.Value;

        Assert.Equal(["https://sam.gov/opportunities/example"], ChatArtifactParser.ExtractSourceUrls(artifact));
    }

    [Fact]
    public void ExactAllowlistRebuildStripsNestedSensitiveFields()
    {
        var input = JsonNode.Parse(ValidArtifactWithItem)!.AsObject();
        input["api_key"] = "top-level-secret";
        var item = input["items"]![0]!.AsObject();
        item["raw_provider_payload"] = new JsonObject { ["contact"] = "private" };
        item["agency"]!.AsObject()["api_key"] = "nested-secret";
        item["source"]!.AsObject()["contact"] = new JsonObject { ["email"] = "private@example.com" };
        input["meta"]!.AsObject()["debug"] = new JsonObject { ["authorization"] = "Bearer secret" };

        var result = ChatArtifactParser.TryExtract(input.ToJsonString(), "get_procurement_opportunities", "call-1")!.Value;
        var serialized = result.GetRawText();

        Assert.DoesNotContain("secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("contact", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("raw_provider_payload", serialized, StringComparison.Ordinal);
        Assert.Equal(new[] { "kind", "schema_version", "status", "generated_at", "items", "meta", "tool_call_id", "tool_name" }, result.EnumerateObject().Select(property => property.Name));
        Assert.Equal("2026-01-10", result.GetProperty("items")[0].GetProperty("posted_at").GetString());
        Assert.Equal("https://sam.gov/opportunities/example", result.GetProperty("items")[0].GetProperty("source").GetProperty("url").GetString()?.TrimEnd('/'));
    }

    [Theory]
    [InlineData("provider", "unknown.example")]
    [InlineData("opportunity_type", "grant")]
    [InlineData("deadline_at", "not-a-date")]
    public void InvalidProviderOpportunityTypeAndDateFailClosed(string property, string value)
    {
        var input = JsonNode.Parse(ValidArtifactWithItem)!.AsObject();
        input["items"]![0]!.AsObject()[property] = value;

        Assert.Null(ChatArtifactParser.TryExtract(input.ToJsonString(), null, null));
    }

    [Fact]
    public void InvalidAmountAndUrlFailClosed()
    {
        var amount = JsonNode.Parse(ValidArtifactWithItem)!.AsObject();
        amount["items"]![0]!["funding"]!["total"] = -1;
        Assert.Null(ChatArtifactParser.TryExtract(amount.ToJsonString(), null, null));

        foreach (var invalidUrl in new[] { "https://user:secret@sam.gov/private", "javascript:alert(1)" })
        {
            var url = JsonNode.Parse(ValidArtifactWithItem)!.AsObject();
            url["items"]![0]!["source"]!["url"] = invalidUrl;
            Assert.Null(ChatArtifactParser.TryExtract(url.ToJsonString(), null, null));
        }
    }

    [Fact]
    public void InvalidCountsLengthsAndPartialErrorsFailClosed()
    {
        var counts = JsonNode.Parse(ValidArtifactWithItem)!.AsObject();
        counts["meta"]!["provider_counts"]!["sam.gov"] = 2;
        Assert.Null(ChatArtifactParser.TryExtract(counts.ToJsonString(), null, null));

        var length = JsonNode.Parse(ValidArtifactWithItem)!.AsObject();
        length["items"]![0]!["title"] = new string('x', 501);
        Assert.Null(ChatArtifactParser.TryExtract(length.ToJsonString(), null, null));

        var errors = JsonNode.Parse(ValidArtifactWithItem)!.AsObject();
        errors["meta"]!["partial_errors"] = new JsonArray(new JsonObject { ["provider"] = "evil.example", ["code"] = "failed", ["retriable"] = false });
        Assert.Null(ChatArtifactParser.TryExtract(errors.ToJsonString(), null, null));
    }

    // -------------------------------------------------------------------
    // contract_awards kind
    // -------------------------------------------------------------------

    private const string ValidContractAwardItem = """
        {"award_id":"CONT_AWD_001","recipient_name":"BRIDGE CORP","award_amount_usd":5000000.0,"awarding_agency":"DEPARTMENT OF TRANSPORTATION","awarding_sub_agency":"FEDERAL HIGHWAY ADMINISTRATION","description":"BRIDGE REHABILITATION PROJECT","place_of_performance":"Austin TX","start_date":"2023-01-15","end_date":"2024-06-30","naics_description":"Highway, Street, and Bridge Construction","contract_type":"Definitive Contract","usaspending_permalink":"https://www.usaspending.gov/award/CONT_AWD_001","source":{"name":"USASpending.gov","retrieved_at":"2026-01-15T12:00:00Z"}}
        """;

    private static string ContractAwardsArtifact(string itemsJson) =>
        "{\"kind\":\"contract_awards\",\"schema_version\":\"1.0\",\"status\":\"ok\",\"generated_at\":\"2026-01-15T12:00:00Z\",\"items\":[" + itemsJson + "],\"meta\":{\"returned_count\":1,\"truncated\":false,\"partial_errors\":[]}}";

    [Fact]
    public void ContractAwardsArtifactIsExtractedWithToolCorrelation()
    {
        var artifact = ContractAwardsArtifact(ValidContractAwardItem);
        var result = ChatArtifactParser.TryExtract(artifact, "get_contract_awards", "call-1");

        Assert.NotNull(result);
        Assert.Equal("contract_awards", result.Value.GetProperty("kind").GetString());
        Assert.Equal("call-1", result.Value.GetProperty("tool_call_id").GetString());
        Assert.Equal("BRIDGE CORP", result.Value.GetProperty("items")[0].GetProperty("recipient_name").GetString());
    }

    [Fact]
    public void ContractAwardsDedupesDuplicateAwardIdsFirstSeenWins()
    {
        var second = ValidContractAwardItem.Replace("CONT_AWD_001", "CONT_AWD_DUP").Replace("BRIDGE CORP", "SECOND SEEN CORP");
        var first = ValidContractAwardItem.Replace("CONT_AWD_001", "CONT_AWD_DUP").Replace("BRIDGE CORP", "FIRST SEEN CORP");
        var artifact = ContractAwardsArtifact($"{first},{second}");
        var result = ChatArtifactParser.TryExtract(artifact, "get_contract_awards", "call-1")!.Value;

        var items = result.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(1, result.GetProperty("meta").GetProperty("returned_count").GetInt32());
        Assert.Equal("FIRST SEEN CORP", items[0].GetProperty("recipient_name").GetString());
    }

    [Fact]
    public void ContractAwardsSourceUrlsUsePermalinkNotSourceObject()
    {
        var artifact = ChatArtifactParser.TryExtract(ContractAwardsArtifact(ValidContractAwardItem), "tool", "call")!.Value;
        Assert.Equal(["https://www.usaspending.gov/award/CONT_AWD_001"], ChatArtifactParser.ExtractSourceUrls(artifact));
    }

    [Fact]
    public void ContractAwardsRejectsWrongSourceName()
    {
        var item = JsonNode.Parse(ValidContractAwardItem)!.AsObject();
        item["source"]!["name"] = "not-usaspending";
        var artifact = ContractAwardsArtifact(item.ToJsonString());
        Assert.Null(ChatArtifactParser.TryExtract(artifact, null, null));
    }
}
