using System.Diagnostics;
using System.Text.Json;
using InfraAdvisor.AgentApi.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfraAdvisor.AgentApi.Tests;

public sealed class PromptTrackingProcessorTests : IDisposable
{
    private readonly ActivitySource _source = new($"test-prompt-tracking-{Guid.NewGuid()}");
    private readonly ActivityListener _listener;
    private readonly PromptTrackingProcessor _processor;

    public PromptTrackingProcessorTests()
    {
        var holder = new PromptHolder(
            "router",
            new DatadogPromptManagementClient(new HttpClient(), NullLogger<DatadogPromptManagementClient>.Instance),
            new PromptVersionFlags(NullLogger<PromptVersionFlags>.Instance),
            "You route questions.",
            NullLogger<PromptHolder>.Instance);
        _processor = new PromptTrackingProcessor(new Dictionary<string, PromptHolder> { ["router"] = holder });
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s == _source,
            ActivityStopped = _processor.OnEnd,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _processor.Dispose();
        _source.Dispose();
    }

    [Fact]
    public void TagsAgentAndChatSpansThatMafNamesAfterStart()
    {
        Activity agent, chat;
        // MAF renames the chat activity OpenTelemetryChatClient already
        // started; the operation and agent id only exist after start.
        using (agent = _source.StartActivity("chat gpt-5.4-mini")!)
        {
            agent.SetTag("gen_ai.operation.name", "invoke_agent");
            agent.SetTag("gen_ai.agent.id", "router");
            using (chat = _source.StartActivity("chat gpt-5.4-mini")!)
                chat.SetTag("gen_ai.operation.name", "chat");
        }

        foreach (var span in new[] { agent, chat })
        {
            var version = Assert.IsType<string>(span.GetTagItem("prompt.version"));
            Assert.StartsWith("v1-", version);
            using var tracking = JsonDocument.Parse(Assert.IsType<string>(span.GetTagItem("_dd.ml_obs.prompt_tracking")));
            Assert.Equal("router", tracking.RootElement.GetProperty("id").GetString());
            Assert.Equal(version, tracking.RootElement.GetProperty("version").GetString());
        }
    }

    [Fact]
    public void LeavesUnknownAgentsAndOtherSpansUntagged()
    {
        Activity unknown, other;
        using (unknown = _source.StartActivity("chat gpt-5.4-mini")!)
        {
            unknown.SetTag("gen_ai.operation.name", "invoke_agent");
            unknown.SetTag("gen_ai.agent.id", "not-registered");
        }
        using (other = _source.StartActivity("classify_domain")!) { }

        Assert.Null(unknown.GetTagItem("prompt.version"));
        Assert.Null(other.GetTagItem("prompt.version"));
    }
}
