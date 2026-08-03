using System.ComponentModel;
using AcaAgenticPoc.Observability;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// ── Config ──────────────────────────────────────────────────────────────────
// Same Env()/EnvOr() helper pattern as services/agent-api-dotnet/Program.cs —
// fail fast on missing required config, sane defaults for optional config.
static string Env(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Required environment variable {name} is not set");

static string EnvOr(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) ?? fallback;

var azureEndpoint = Env("AZURE_OPENAI_ENDPOINT");
var azureApiKey = Env("AZURE_OPENAI_API_KEY");
var azureDeployment = EnvOr("AZURE_OPENAI_DEPLOYMENT", "gpt-4.1-mini");

var builder = WebApplication.CreateBuilder(args);

TelemetrySetup.Configure(builder);

// ── Agent: one Azure OpenAI chat client + exactly one trivial tool ─────────
// Deliberately minimal — no MCP, no Redis-backed session, no tool refresh
// machinery. This app exists to demonstrate the OTel span tree
// (invoke_agent → execute_tool → chat), not to be a real product surface.
[Description("Returns the current UTC date and time. Use this whenever the user asks what time or date it is.")]
static string GetCurrentTimeUtc() => DateTimeOffset.UtcNow.ToString("u");

var chatClient = new AzureOpenAIClient(new Uri(azureEndpoint), new AzureKeyCredential(azureApiKey))
    .GetChatClient(azureDeployment)
    .AsIChatClient()
    .AsBuilder()
    .UseFunctionInvocation()
    .UseOpenTelemetry(configure: cfg => cfg.EnableSensitiveData = true)
    .Build();

var agent = new ChatClientAgent(
        chatClient,
        new ChatClientAgentOptions
        {
            Name = "aca-agentic-poc",
            ChatOptions = new ChatOptions
            {
                Instructions = "You are a minimal demo agent. Answer concisely. " +
                    "Use the get_current_time_utc tool whenever the user asks about the current time or date.",
                Tools = [AIFunctionFactory.Create(GetCurrentTimeUtc)],
            },
        })
    .AsBuilder()
    .UseOpenTelemetry(sourceName: TelemetrySetup.ActivitySourceName, configure: cfg => cfg.EnableSensitiveData = true)
    .Build();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/run", async (RunRequest body, CancellationToken ct) =>
{
    var response = await agent.RunAsync(body.Query, cancellationToken: ct);
    return Results.Ok(new RunResponse(response.Text ?? ""));
});

app.Run();

record RunRequest(string Query);
record RunResponse(string Answer);
