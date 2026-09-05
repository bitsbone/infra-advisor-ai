using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace InfraAdvisor.AgentApi.Tests;

/// <summary>
/// Boots the real app end-to-end via <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// Exists specifically to catch what unit tests of individual classes cannot:
/// a minimal-API endpoint whose parameter can't be bound (e.g. an unregistered
/// service type gets inferred as an invalid request body parameter). ASP.NET
/// Core only validates this when the endpoint route table is actually built,
/// which happens during host startup — so the only way to catch it is to
/// start the host, exactly like this test does.
///
/// 2026-09-04 incident: GET /prompts/status injected PromptVersionFlags,
/// which was never registered in DI. The app compiled fine and every unit
/// test in this project passed; it only failed at runtime in the real
/// cluster, taking down the whole host on every restart. This test would
/// have caught it in CI before the image was ever built.
/// </summary>
public class StartupTests
{
    private static WebApplicationFactory<Program> CreateFactory()
    {
        // Program.cs fails fast (throws before the host can start) if these
        // two are missing; every other config value has a safe default.
        // Real backing services (Postgres, Redis, Kafka, the MCP server)
        // are not required for the host to start — each integration point
        // in Program.cs is either lazily resolved (Redis, MCP) or fails
        // open with a logged warning (Postgres schema init, Kafka
        // subscribe) rather than crashing startup. That fail-open behavior
        // is exactly what lets this test run without standing up the full
        // dependency stack, while still exercising the real startup path —
        // including route-table construction, DI resolution order, and
        // every hosted service's ExecuteAsync.
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://mock.openai.azure.com");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "mock-key");
        Environment.SetEnvironmentVariable("JWT_SECRET", "test-secret-not-for-real-use");

        // Default HostOptions.ShutdownTimeout is 30s — some hosted service
        // (Kafka's consumer loop is the likely one; its native client call
        // doesn't reliably observe the stopping CancellationToken) doesn't
        // stop promptly, so disposal below waits out most of that timeout
        // every run (measured: ~45s total without this, ~17s with it).
        // Shortening it only affects this test process, not the real app.
        var factory = new WebApplicationFactory<Program>();
        return factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2))));
    }

    [Fact]
    public async Task Host_starts_and_builds_its_full_endpoint_route_table()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // /livez is deliberately shallow (never touches MCP/Redis/the LLM —
        // see Program.cs) but hitting ANY endpoint forces ASP.NET Core to
        // compile the entire route table up front, which is what validates
        // every other minimal-API endpoint's parameter bindings too —
        // including ones this test never calls directly, like
        // GET /prompts/status.
        var response = await client.GetAsync("/livez");

        response.EnsureSuccessStatusCode();
    }
}
