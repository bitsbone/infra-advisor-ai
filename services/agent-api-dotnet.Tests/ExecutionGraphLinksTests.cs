using System.Diagnostics;
using InfraAdvisor.AgentApi.Observability;
using Xunit;

namespace InfraAdvisor.AgentApi.Tests;

public sealed class ExecutionGraphLinksTests : IDisposable
{
    private readonly ActivitySource _source = new($"test-execution-graph-{Guid.NewGuid()}");
    private readonly ActivityListener _listener;
    private readonly ExecutionGraphLinks _processor = new();

    public ExecutionGraphLinksTests()
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

    private Activity Start(string name) => _source.StartActivity(name, ActivityKind.Internal)!;

    // MAF starts no invoke_agent activity of its own: OpenTelemetryAgent
    // renames the chat activity OpenTelemetryChatClient already started.
    private Activity StartAgent()
    {
        var activity = Start("chat gpt-5.4-mini");
        activity.SetTag("gen_ai.operation.name", "invoke_agent");
        return activity;
    }

    private static (ActivitySpanId SpanId, string? From, string? To) Link(Activity activity)
    {
        var link = Assert.Single(activity.Links);
        var tags = link.Tags!.ToDictionary(t => t.Key, t => t.Value as string);
        return (link.Context.SpanId, tags["from"], tags["to"]);
    }

    [Fact]
    public void ChainsEachDataStepToThePreviousOneAndSkipsTheRest()
    {
        using var workflow = Start(ExecutionGraphLinks.WorkflowOperationName);
        using (Start("ai_guard.evaluate")) { }
        using (Start("classify_domain")) { }

        Activity retrieve, router, specialist, nested;
        using (retrieve = Start("retrieve_best_practices")) { }
        using (router = StartAgent())
        {
            // A plain chat call inside the agent is not a pipeline step.
            using var chat = Start("chat gpt-5.4-mini");
        }
        using (specialist = StartAgent())
        {
            // A nested agent belongs to the specialist, not the pipeline.
            using (nested = StartAgent()) { }
        }

        Assert.Empty(retrieve.Links);
        Assert.Equal((retrieve.SpanId, "output", "input"), Link(router));
        Assert.Equal((router.SpanId, "output", "input"), Link(specialist));
        Assert.Empty(nested.Links);
    }

    [Fact]
    public void LeavesStepsOutsideAWorkflowUnlinked()
    {
        using (StartAgent()) { }
        Activity second;
        using (second = StartAgent()) { }
        Assert.Empty(second.Links);
    }
}
