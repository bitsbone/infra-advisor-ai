using System.Net;
using System.Text;
using System.Text.Json;
using InfraAdvisor.McpServer.Tools;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfraAdvisor.McpServer.Tests;

public sealed class ProcurementOpportunitiesToolTests
{
    private static readonly string[] ItemProperties = ["agency", "classifications", "data_quality", "deadline_at", "funding", "id", "location", "opportunity_type", "posted_at", "provider", "provider_id", "source", "status", "summary", "title"];

    [Fact]
    public async Task ProducesBoundedSanitizedV1ArtifactFromBothProviders()
    {
        var priorKey = Environment.GetEnvironmentVariable("SAMGOV_API_KEY");
        Environment.SetEnvironmentVariable("SAMGOV_API_KEY", "test-key-not-real");
        try
        {
            var logger = new CaptureLogger<ProcurementOpportunitiesTool>();
            var tool = new ProcurementOpportunitiesTool(new StubHttpClientFactory(), logger);
            var json = await tool.GetProcurementOpportunitiesAsync("water resilience", geography: "Texas", limit: 1);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("procurement_opportunities", root.GetProperty("kind").GetString());
            Assert.Equal("1.0", root.GetProperty("schema_version").GetString());
            Assert.Single(root.GetProperty("items").EnumerateArray());
            Assert.True(root.GetProperty("meta").GetProperty("truncated").GetBoolean());
            var sourceUrl = root.GetProperty("items")[0].GetProperty("source").GetProperty("url").GetString();
            Assert.NotNull(sourceUrl);
            Assert.DoesNotContain("api_key", sourceUrl, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('#', sourceUrl!);
            Assert.DoesNotContain("test-key-not-real", json);

            var fields = Assert.Single(logger.Entries);
            Assert.Equal("procurement.artifact.normalized", fields["Event"]);
            Assert.Equal("get_procurement_opportunities", fields["ToolName"]);
            Assert.Equal("procurement_opportunities", fields["ArtifactKind"]);
            Assert.Equal("1.0", fields["ArtifactSchemaVersion"]);
            Assert.Equal(1, fields["ArtifactReturnedCount"]);
            Assert.True(Convert.ToDouble(fields["DurationMs"]) >= 0);
            var logged = JsonSerializer.Serialize(fields);
            Assert.DoesNotContain("water resilience", logged);
            Assert.DoesNotContain("test-key-not-real", logged);
            Assert.DoesNotContain("api_key", logged, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SAMGOV_API_KEY", priorKey);
        }
    }

    [Fact]
    public async Task FiltersFundingAndRebuildsAdversarialProviderDataToExactV1Shape()
    {
        var priorKey = Environment.GetEnvironmentVariable("SAMGOV_API_KEY");
        Environment.SetEnvironmentVariable("SAMGOV_API_KEY", "test-key-not-real");
        try
        {
            var tool = new ProcurementOpportunitiesTool(new StubHttpClientFactory(), new CaptureLogger<ProcurementOpportunitiesTool>());
            var json = await tool.GetProcurementOpportunitiesAsync("water resilience", min_value_usd: 1_000_000, max_value_usd: 10_000_000);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var item = Assert.Single(root.GetProperty("items").EnumerateArray());

            Assert.Equal("grants.gov", item.GetProperty("provider").GetString());
            Assert.Equal("GRANT-1", item.GetProperty("provider_id").GetString());
            Assert.Equal(ItemProperties, item.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal(new[] { "code", "name" }, item.GetProperty("agency").EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal(new[] { "assistance_listing", "naics", "set_aside" }, item.GetProperty("classifications").EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal(new[] { "currency", "expected_awards", "maximum", "minimum", "total" }, item.GetProperty("funding").EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal(new[] { "retrieved_at", "url" }, item.GetProperty("source").EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal(5_000_000, item.GetProperty("funding").GetProperty("total").GetDouble());
            Assert.Equal(new[] { "66.458" }, item.GetProperty("classifications").GetProperty("assistance_listing").EnumerateArray().Select(value => value.GetString()).ToArray());
            Assert.DoesNotContain("PROVIDER-SECRET-MUST-NOT-ESCAPE", json);
            Assert.DoesNotContain("contactInformation", json);
            Assert.Equal(1, root.GetProperty("meta").GetProperty("returned_count").GetInt32());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SAMGOV_API_KEY", priorKey);
        }
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<Dictionary<string, object?>> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                Entries.Add(values.Where(value => value.Key != "{OriginalFormat}").ToDictionary(value => value.Key, value => value.Value));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "api.sam.gov") Assert.Contains("state=TX", request.RequestUri.Query);
            var body = request.RequestUri!.Host == "api.sam.gov"
                ? """{"opportunitiesData":[{"noticeId":"SAM-1","title":"Water project","type":"Solicitation","fullParentPathName":"Example Agency","postedDate":"2026-01-01","responseDeadLine":"2026-03-01","uiLink":"https://sam.gov/opp/SAM-1?api_key=echoed#details","naicsCode":"237110","contactInformation":{"email":"PROVIDER-SECRET-MUST-NOT-ESCAPE"},"providerPayload":{"api_key":"PROVIDER-SECRET-MUST-NOT-ESCAPE"}}]}"""
                : """{"data":{"oppHits":[{"id":"GRANT-1","title":"Resilience grant","agencyName":"Example Agency","agencyCode":"EA","oppStatus":"posted","openDate":"2026-01-01","closeDate":"2026-04-01","estimatedTotalProgramFunding":5000000,"expectedNumberOfAwards":10,"alnist":["66.458","invalid","PROVIDER-SECRET-MUST-NOT-ESCAPE"],"contactInformation":{"email":"PROVIDER-SECRET-MUST-NOT-ESCAPE"}},{"id":"GRANT-2","title":"Large grant","agencyName":"Example Agency","oppStatus":"posted","openDate":"2026-01-01","closeDate":"2026-05-01","estimatedTotalProgramFunding":15000000,"alnist":["66.458"]}]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
