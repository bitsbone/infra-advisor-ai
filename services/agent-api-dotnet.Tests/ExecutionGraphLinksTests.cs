using System.Diagnostics;
using InfraAdvisor.AgentApi.Observability;
using Xunit;

namespace InfraAdvisor.AgentApi.Tests;

public sealed class ExecutionGraphLinksTests : IDisposable
{
    private readonly ActivitySource _source = new($"test-execution-graph-{Guid.NewGuid()}");
    private readonly ActivityListener _listener;

    public ExecutionGraphLinksTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s == _source,
            ActivityStarted = ExecutionGraphLinks.OnStarted,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    private Activity Start(string name) => _source.StartActivity(name, ActivityKind.Internal)!;

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

        Activity retrieve, router, specialist;
        using (retrieve = Start("retrieve_best_practices")) { }
        using (router = Start("invoke_agent")) { }
        using (specialist = Start("invoke_agent"))
        {
            // A nested agent belongs to the specialist, not the pipeline.
            using var nested = Start("invoke_agent");
            Assert.Empty(nested.Links);
        }

        Assert.Empty(retrieve.Links);
        Assert.Equal((retrieve.SpanId, "output", "input"), Link(router));
        Assert.Equal((router.SpanId, "output", "input"), Link(specialist));
    }

    [Fact]
    public void LeavesStepsOutsideAWorkflowUnlinked()
    {
        using (Start("invoke_agent")) { }
        using var second = Start("invoke_agent");
        Assert.Empty(second.Links);
    }
}
