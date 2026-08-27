using System.Text.Json;
using InfraAdvisor.Mobile.Models;

namespace InfraAdvisor.Mobile.Tests;

public sealed class ArtifactPresentationTests
{
    [Fact]
    public void MapsSanitizedProcurementFixtureAndRejectsNonHttpLinks()
    {
        var artifact = Parse("""
            {"kind":"procurement_opportunities","schema_version":"1.0","status":"ok","generated_at":"2026-08-26T12:00:00Z","items":[{"id":"sam.gov:sample","provider":"sam.gov","provider_id":"sample","opportunity_type":"contract","title":"Sample resilience assessment","agency":{"name":"Example Agency","code":null},"summary":"Sanitized example.","status":"posted","posted_at":"2026-08-01","deadline_at":"2026-09-30","location":{"state_code":"TX","state_name":"Texas","city":null},"classifications":{"naics":["541330"],"assistance_listing":[],"set_aside":null},"funding":{"currency":"USD","minimum":null,"maximum":1000000,"total":null,"expected_awards":null},"source":{"url":"javascript:alert(1)","retrieved_at":"2026-08-26T12:00:00Z"},"data_quality":{"missing_fields":[]}}],"meta":{"returned_count":1,"provider_counts":{"sam.gov":1},"truncated":false,"partial_errors":[]}}
            """);

        var card = Assert.Single(ArtifactPresentationMapper.ToCards(artifact));

        Assert.Equal("Sample resilience assessment", card.Title);
        Assert.Equal("sam.gov", card.Source);
        Assert.False(card.HasLink);
    }

    [Fact]
    public void UnknownVersionAndMalformedItemsAreIgnoredWithoutLosingValidItems()
    {
        var unknown = Parse("""{"kind":"future_map_layer","schema_version":"2.0","status":"ok","generated_at":null,"items":[{"shape":"future"}],"meta":{}}""");
        var mixed = Parse("""
            {"kind":"procurement_opportunities","schema_version":"1.0","status":"partial","generated_at":"2026-08-26T12:00:00Z","items":[{"unexpected":true},{"id":"grants.gov:sample","provider":"grants.gov","provider_id":"sample","opportunity_type":"grant","title":"Sample emergency management grant","agency":{"name":"Example Agency","code":null},"summary":"Sanitized example.","status":"posted","posted_at":null,"deadline_at":null,"location":{"state_code":"TX","state_name":"Texas","city":null},"classifications":{"naics":[],"assistance_listing":["97.047"],"set_aside":null},"funding":{"currency":"USD","minimum":null,"maximum":null,"total":null,"expected_awards":null},"source":{"url":"https://grants.gov/example","retrieved_at":null},"data_quality":{"missing_fields":[]}}],"meta":{"returned_count":2,"provider_counts":{"grants.gov":2},"truncated":false,"partial_errors":[]}}
            """);

        Assert.Empty(ArtifactPresentationMapper.ToCards(unknown));
        Assert.Equal("Sample emergency management grant", Assert.Single(ArtifactPresentationMapper.ToCards(mixed)).Title);
    }

    private static ChatArtifact Parse(string json) => JsonSerializer.Deserialize<ChatArtifact>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}
