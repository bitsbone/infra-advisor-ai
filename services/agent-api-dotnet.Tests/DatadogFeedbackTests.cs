using System.Net;
using System.Text.Json;
using InfraAdvisor.AgentApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfraAdvisor.AgentApi.Tests;

public sealed class DatadogFeedbackTests
{
    [Fact]
    public async Task FeedbackUsesDedicatedEventWithSubmitterAndOneSpanTarget()
    {
        var previousKey = Environment.GetEnvironmentVariable("DD_API_KEY");
        var previousSite = Environment.GetEnvironmentVariable("DD_SITE");
        try
        {
            Environment.SetEnvironmentVariable("DD_API_KEY", "test-only-key");
            Environment.SetEnvironmentVariable("DD_SITE", "us3.datadoghq.com");
            var handler = new CaptureHandler();
            var client = new DatadogEvalsClient(new HttpClient(handler), NullLogger<DatadogEvalsClient>.Instance, new EvalSubmissionLog());

            await client.SubmitFeedbackAsync("123", "456", "positive", "user-789", CancellationToken.None);

            Assert.Equal("https://api.us3.datadoghq.com/api/intake/llm-obs/v2/eval-metric", handler.RequestUri);
            Assert.Equal("test-only-key", handler.ApiKey);
            using var document = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
            var metric = document.RootElement.GetProperty("data").GetProperty("attributes").GetProperty("metrics")[0];
            Assert.Equal("feedback", metric.GetProperty("event_kind").GetString());
            Assert.Equal("456", metric.GetProperty("span_id").GetString());
            Assert.Equal("user-789", metric.GetProperty("submitter").GetProperty("id").GetString());
            Assert.Equal("user", metric.GetProperty("submitter").GetProperty("type").GetString());
            Assert.Equal("pass", metric.GetProperty("assessment").GetString());
            Assert.False(metric.TryGetProperty("join_on", out _));
            Assert.False(metric.TryGetProperty("trace_id", out _));
            Assert.False(metric.TryGetProperty("session_id", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DD_API_KEY", previousKey);
            Environment.SetEnvironmentVariable("DD_SITE", previousSite);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? RequestUri { get; private set; }
        public string? ApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            ApiKey = request.Headers.GetValues("DD-API-KEY").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }
}
