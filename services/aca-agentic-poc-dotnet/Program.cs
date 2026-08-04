using System.ComponentModel;
using System.Text;
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

// ── Auth: single-user HTTP Basic Auth ───────────────────────────────────────
// Deliberately not the auth-api/JWT pattern agent-api-dotnet uses — this is
// an isolated demo, not part of the main product's user base. The browser's
// native login prompt on a 401 challenge is the simplest possible gate.
// /health is excluded so ACA's platform probes keep passing.
var uiUsername = Env("POC_UI_USERNAME");
var uiPassword = Env("POC_UI_PASSWORD");

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health")
    {
        await next();
        return;
    }

    var header = context.Request.Headers.Authorization.ToString();
    if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));
        var parts = decoded.Split(':', 2);
        if (parts.Length == 2 && parts[0] == uiUsername && parts[1] == uiPassword)
        {
            await next();
            return;
        }
    }

    context.Response.Headers.WWWAuthenticate = "Basic realm=\"aca-agentic-poc\"";
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Values embedded in browser JS are inherently public — the RUM client token
// is designed for this (Datadog scopes it to intake-write-only). Reusing the
// same RUM Application as infra-advisor-ui, with a distinct `service` per
// OTel path so sessions are separable the same way APM traces already are.
app.MapGet("/rum-config.js", () =>
{
    var config = new
    {
        applicationId = Env("DD_RUM_APPLICATION_ID"),
        clientToken = Env("DD_RUM_CLIENT_TOKEN"),
        site = EnvOr("DD_RUM_SITE", "us3.datadoghq.com"),
        service = EnvOr("OTEL_SERVICE_NAME", "aca-agentic-poc") + "-ui",
        env = EnvOr("DD_ENV", "dev"),
        version = EnvOr("DD_VERSION", "latest"),
    };
    var json = System.Text.Json.JsonSerializer.Serialize(config);
    return Results.Text($"window.DD_RUM_CONFIG = {json};", "application/javascript");
});

app.MapPost("/run", async (RunRequest body, CancellationToken ct) =>
{
    var response = await agent.RunAsync(body.Query, cancellationToken: ct);
    return Results.Ok(new RunResponse(response.Text ?? ""));
});

app.Run();

record RunRequest(string Query);
record RunResponse(string Answer);
