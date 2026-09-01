using System.Net;
using System.Text;
using System.Text.Json;
using InfraAdvisor.McpServer.Tools;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfraAdvisor.McpServer.Tests;

public sealed class ContractAwardsToolTests
{
    [Fact]
    public async Task ProducesBoundedV1ArtifactFromUsaspending()
    {
        var tool = new ContractAwardsTool(new StubHttpClientFactory(TwoAwardsBody), new NullLogger());
        var json = await tool.GetContractAwardsAsync("bridge rehabilitation Texas");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("contract_awards", root.GetProperty("kind").GetString());
        Assert.Equal("1.0", root.GetProperty("schema_version").GetString());
        Assert.Equal("ok", root.GetProperty("status").GetString());
        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(2, root.GetProperty("meta").GetProperty("returned_count").GetInt32());
        Assert.Equal("BRIDGE CORP", items[0].GetProperty("recipient_name").GetString());
        Assert.Contains("CONT_AWD_001", items[0].GetProperty("usaspending_permalink").GetString());
    }

    [Fact]
    public async Task DedupesDuplicateAwardIdsFirstSeenWins()
    {
        var tool = new ContractAwardsTool(new StubHttpClientFactory(DuplicateAwardsBody), new NullLogger());
        var json = await tool.GetContractAwardsAsync("bridge rehabilitation Texas");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(2, root.GetProperty("meta").GetProperty("returned_count").GetInt32());
        var dup = items.Single(i => i.GetProperty("award_id").GetString() == "CONT_AWD_DUP");
        Assert.Equal("FIRST SEEN CORP", dup.GetProperty("recipient_name").GetString());
    }

    [Fact]
    public async Task HttpErrorProducesErrorStatusArtifact()
    {
        var tool = new ContractAwardsTool(new StubHttpClientFactory(null, HttpStatusCode.InternalServerError), new NullLogger());
        var json = await tool.GetContractAwardsAsync("water infrastructure");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("contract_awards", root.GetProperty("kind").GetString());
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Empty(root.GetProperty("items").EnumerateArray());
        var errors = root.GetProperty("meta").GetProperty("partial_errors").EnumerateArray().ToList();
        var error = Assert.Single(errors);
        Assert.Equal("http_500", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retriable").GetBoolean());
    }

    private const string TwoAwardsBody = """
        {"results":[
            {"Award ID":"CONT_AWD_001","Recipient Name":"BRIDGE CORP","Award Amount":5000000.0,"Awarding Agency":"DEPARTMENT OF TRANSPORTATION","Awarding Sub Agency":"FEDERAL HIGHWAY ADMINISTRATION","Description":"BRIDGE REHABILITATION PROJECT","Start Date":"2023-01-15","End Date":"2024-06-30","Contract Award Type":"D","Place of Performance State Code":"TX","Place of Performance City Name":"Austin","naics_description":"Highway, Street, and Bridge Construction"},
            {"Award ID":"CONT_AWD_002","Recipient Name":"ROAD BUILDERS INC","Award Amount":2000000.0,"Awarding Agency":"DEPARTMENT OF TRANSPORTATION","Awarding Sub Agency":"FEDERAL HIGHWAY ADMINISTRATION","Description":"HIGHWAY EXPANSION","Start Date":"2023-01-15","End Date":"2024-06-30","Contract Award Type":"D","Place of Performance State Code":"TX","Place of Performance City Name":"Austin","naics_description":"Highway, Street, and Bridge Construction"}
        ]}
        """;

    private const string DuplicateAwardsBody = """
        {"results":[
            {"Award ID":"CONT_AWD_DUP","Recipient Name":"FIRST SEEN CORP","Award Amount":5000000.0,"Awarding Agency":"DOT","Awarding Sub Agency":"FHWA","Description":"BRIDGE","Start Date":"2023-01-15","End Date":"2024-06-30","Contract Award Type":"D","Place of Performance State Code":"TX","Place of Performance City Name":"Austin","naics_description":"Bridge Construction"},
            {"Award ID":"CONT_AWD_DUP","Recipient Name":"SECOND SEEN CORP","Award Amount":5000000.0,"Awarding Agency":"DOT","Awarding Sub Agency":"FHWA","Description":"BRIDGE (dup)","Start Date":"2023-01-15","End Date":"2024-06-30","Contract Award Type":"D","Place of Performance State Code":"TX","Place of Performance City Name":"Austin","naics_description":"Bridge Construction"},
            {"Award ID":"CONT_AWD_UNIQUE","Recipient Name":"UNIQUE CORP","Award Amount":1000000.0,"Awarding Agency":"DOT","Awarding Sub Agency":"FHWA","Description":"ROAD","Start Date":"2023-01-15","End Date":"2024-06-30","Contract Award Type":"D","Place of Performance State Code":"TX","Place of Performance City Name":"Austin","naics_description":"Road Construction"}
        ]}
        """;

    private sealed class NullLogger : ILogger<ContractAwardsTool>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    private sealed class StubHttpClientFactory(string? body, HttpStatusCode statusCode = HttpStatusCode.OK) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(body, statusCode));
    }

    private sealed class StubHandler(string? body, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = body ?? """{"results":[]}""";
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        }
    }
}
