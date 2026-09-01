using System.Net;
using System.Text;
using System.Text.Json;
using InfraAdvisor.AgentApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfraAdvisor.AgentApi.Tests;

public sealed class DatadogPromptManagementClientTests
{
    [Fact]
    public async Task DisabledWithoutEnvVarReturnsFallbackWithoutCallingHttp()
    {
        var previousEnabled = Environment.GetEnvironmentVariable("DD_PROMPT_MANAGEMENT_ENABLED");
        var previousKey = Environment.GetEnvironmentVariable("DD_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("DD_PROMPT_MANAGEMENT_ENABLED", null);
            Environment.SetEnvironmentVariable("DD_API_KEY", "test-only-key");
            var handler = new UnreachableHandler();
            var client = new DatadogPromptManagementClient(new HttpClient(handler), NullLogger<DatadogPromptManagementClient>.Instance);

            var result = await client.GetPromptTemplateAsync("infra-advisor-system-prompt", fallback: "local fallback prompt");

            Assert.Equal("local fallback prompt", result.Template);
            Assert.Equal("fallback", result.Source);
            Assert.False(handler.WasCalled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DD_PROMPT_MANAGEMENT_ENABLED", previousEnabled);
            Environment.SetEnvironmentVariable("DD_API_KEY", previousKey);
        }
    }

    [Fact]
    public async Task EnabledAndSuccessfulFetchReturnsRegistryTemplateAndVersion()
    {
        var previousEnabled = Environment.GetEnvironmentVariable("DD_PROMPT_MANAGEMENT_ENABLED");
        var previousKey = Environment.GetEnvironmentVariable("DD_API_KEY");
        var previousSite = Environment.GetEnvironmentVariable("DD_SITE");
        try
        {
            Environment.SetEnvironmentVariable("DD_PROMPT_MANAGEMENT_ENABLED", "true");
            Environment.SetEnvironmentVariable("DD_API_KEY", "test-only-key");
            Environment.SetEnvironmentVariable("DD_SITE", "us3.datadoghq.com");
            var handler = new StubHandler(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                prompt_id = "infra-advisor-system-prompt",
                template = "registry-managed prompt text",
                user_version = "v2",
            }));
            var client = new DatadogPromptManagementClient(new HttpClient(handler), NullLogger<DatadogPromptManagementClient>.Instance);

            var result = await client.GetPromptTemplateAsync("infra-advisor-system-prompt", fallback: "local fallback prompt");

            Assert.Equal("registry-managed prompt text", result.Template);
            Assert.Equal("v2", result.Version);
            Assert.Equal("registry", result.Source);
            Assert.Equal(
                "https://api.us3.datadoghq.com/api/unstable/llm-obs/v1/prompts/infra-advisor-system-prompt",
                handler.RequestUri);
            Assert.Equal("test-only-key", handler.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DD_PROMPT_MANAGEMENT_ENABLED", previousEnabled);
            Environment.SetEnvironmentVariable("DD_API_KEY", previousKey);
            Environment.SetEnvironmentVariable("DD_SITE", previousSite);
        }
    }

    [Fact]
    public async Task EnabledButHttpErrorFailsOpenToFallback()
    {
        var previousEnabled = Environment.GetEnvironmentVariable("DD_PROMPT_MANAGEMENT_ENABLED");
        var previousKey = Environment.GetEnvironmentVariable("DD_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("DD_PROMPT_MANAGEMENT_ENABLED", "true");
            Environment.SetEnvironmentVariable("DD_API_KEY", "test-only-key");
            var handler = new StubHandler(HttpStatusCode.NotFound, "{}");
            var client = new DatadogPromptManagementClient(new HttpClient(handler), NullLogger<DatadogPromptManagementClient>.Instance);

            var result = await client.GetPromptTemplateAsync("infra-advisor-system-prompt", fallback: "local fallback prompt");

            Assert.Equal("local fallback prompt", result.Template);
            Assert.Equal("fallback", result.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DD_PROMPT_MANAGEMENT_ENABLED", previousEnabled);
            Environment.SetEnvironmentVariable("DD_API_KEY", previousKey);
        }
    }

    [Fact]
    public async Task EnabledButThrowingHandlerFailsOpenToFallback()
    {
        var previousEnabled = Environment.GetEnvironmentVariable("DD_PROMPT_MANAGEMENT_ENABLED");
        var previousKey = Environment.GetEnvironmentVariable("DD_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("DD_PROMPT_MANAGEMENT_ENABLED", "true");
            Environment.SetEnvironmentVariable("DD_API_KEY", "test-only-key");
            var client = new DatadogPromptManagementClient(new HttpClient(new ThrowingHandler()), NullLogger<DatadogPromptManagementClient>.Instance);

            var result = await client.GetPromptTemplateAsync("infra-advisor-system-prompt", fallback: "local fallback prompt");

            Assert.Equal("local fallback prompt", result.Template);
            Assert.Equal("fallback", result.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DD_PROMPT_MANAGEMENT_ENABLED", previousEnabled);
            Environment.SetEnvironmentVariable("DD_API_KEY", previousKey);
        }
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("HTTP should never be called when disabled.");
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            ApiKey = request.Headers.TryGetValues("DD-API-KEY", out var values) ? values.Single() : null;
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated transport failure");
    }
}
