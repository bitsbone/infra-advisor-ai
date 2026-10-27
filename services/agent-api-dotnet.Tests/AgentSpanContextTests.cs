using System.Diagnostics;
using InfraAdvisor.AgentApi.Observability;
using Xunit;

namespace InfraAdvisor.AgentApi.Tests;

public sealed class AgentSpanContextTests : IDisposable
{
    private readonly ActivitySource _source = new($"test-agent-span-{Guid.NewGuid()}");
    private readonly ActivityListener _listener;
    private readonly AgentSpanContext.Processor _processor = new();

    public AgentSpanContextTests()
    {
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

    // Like MAF: the agent span starts as a chat activity, is renamed after
    // start, and ends deep inside async code the caller only awaits.
    private async Task<ActivitySpanId> RunAgentAsync()
    {
        await Task.Yield();
        using var activity = _source.StartActivity("chat gpt-5.4-mini")!;
        activity.SetTag("gen_ai.operation.name", "invoke_agent");
        await Task.Yield();
        return activity.SpanId;
    }

    [Fact]
    public async Task CallerSeesTheLastAgentToEnd()
    {
        AgentSpanContext.BeginRequest();
        await RunAgentAsync();                       // router
        var specialist = await RunAgentAsync();

        var captured = Assert.IsType<AgentSpanContext.Captured>(AgentSpanContext.Current);
        Assert.Equal(specialist.ToHexString(), captured.SpanIdHex);
    }

    [Fact]
    public async Task NothingIsCapturedWithoutARequestSlot()
    {
        await Task.Run(async () =>
        {
            await RunAgentAsync();
            Assert.Null(AgentSpanContext.Current);
        });
    }
}
