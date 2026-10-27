using System.Diagnostics;

namespace InfraAdvisor.AgentApi.Observability;

// Draws the query-processing data flow in DD Agent Observability's
// execution graph. The graph only connects sibling spans that carry an
// OTel span link with "from"/"to" attributes
// (https://docs.datadoghq.com/llm_observability/instrument/otel_instrumentation/);
// without one, each step renders as an unconnected box in no particular order.
//
// The steps whose output becomes the next step's input form one chain:
//   transcribe_audio → retrieve_best_practices → router → specialist-*
// (the transcript joins the query, retrieved context is prepended to it, and
// the router hands off to the specialist). classify_domain and
// ai_guard.evaluate only tag or gate the request, so they stay unlinked.
//
// Called from the ActivityListener in Program.cs, which already sees both our
// own spans and MAF's invoke_agent spans as they start. The chain's last step
// is stored on the query-processing Activity, so concurrent requests never
// share it and the streaming path's ExecutionContext restores don't matter.
public static class ExecutionGraphLinks
{
    public const string WorkflowOperationName = "query-processing";
    private const string LastStepProperty = "infra.execution_graph.last_step";

    private static readonly HashSet<string> ChainedOperations =
        new() { "transcribe_audio", "retrieve_best_practices", "invoke_agent" };

    public static void OnStarted(Activity activity)
    {
        if (!ChainedOperations.Contains(activity.OperationName)) return;

        // Only direct pipeline steps chain: an agent invoked inside another
        // step (a nested tool agent, say) belongs to that step's own graph.
        Activity? workflow = null;
        for (var parent = activity.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.OperationName == WorkflowOperationName) { workflow = parent; break; }
            if (ChainedOperations.Contains(parent.OperationName)) return;
        }
        if (workflow is null) return;

        if (workflow.GetCustomProperty(LastStepProperty) is ActivityContext previous)
            activity.AddLink(new ActivityLink(previous, new ActivityTagsCollection { ["from"] = "output", ["to"] = "input" }));
        workflow.SetCustomProperty(LastStepProperty, activity.Context);
    }
}
