using System.Net;
using System.Text;
using InfraAdvisor.McpServer.Tools;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfraAdvisor.McpServer.Tests;

public sealed class TelemetryPrivacyToolTests
{
    [Fact]
    public async Task ContractAwardsLogsExcludeQueryGeographyAndProviderBody()
    {
        var logger = new CaptureLogger<ContractAwardsTool>();
        var tool = new ContractAwardsTool(new SingleResponseFactory(HttpStatusCode.UnprocessableEntity, "PRIVATE-USASPENDING-BODY"), logger);

        await tool.GetContractAwardsAsync("PRIVATE-CONTRACT-QUERY", geography: "PRIVATE-GEOGRAPHY");

        var logged = string.Join('\n', logger.Messages);
        Assert.DoesNotContain("PRIVATE-CONTRACT-QUERY", logged);
        Assert.DoesNotContain("PRIVATE-GEOGRAPHY", logged);
        Assert.DoesNotContain("PRIVATE-USASPENDING-BODY", logged);
    }

    [Fact]
    public async Task WebProcurementLogsExcludeFiltersAndProviderBody()
    {
        var priorEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var priorKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "test-key-not-real");
        try
        {
            var logger = new CaptureLogger<WebProcurementSearchTool>();
            var tool = new WebProcurementSearchTool(new SingleResponseFactory(HttpStatusCode.BadRequest, "PRIVATE-AZURE-BODY"), logger);

            await tool.SearchWebProcurementAsync("PRIVATE-WEB-QUERY", geography: "PRIVATE-GEOGRAPHY", sector: "water");

            var logged = string.Join('\n', logger.Messages);
            Assert.DoesNotContain("PRIVATE-WEB-QUERY", logged);
            Assert.DoesNotContain("PRIVATE-GEOGRAPHY", logged);
            Assert.DoesNotContain("PRIVATE-AZURE-BODY", logged);
            Assert.DoesNotContain("water", logged, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", priorEndpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", priorKey);
        }
    }

    private sealed class SingleResponseFactory(HttpStatusCode status, string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(status, body));

        private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
