using System.Diagnostics;
using OpenTelemetry;

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
// Links are added as each step ENDS, not when it starts: MAF has no
// invoke_agent activity of its own. OpenTelemetryAgent renames the "chat"
// activity that Microsoft.Extensions.AI's OpenTelemetryChatClient already
// started and sets gen_ai.operation.name=invoke_agent on it afterwards
// (ilspycmd, Microsoft.Agents.AI 1.20.0: UpdateCurrentActivity), so at start
// the router and specialist are indistinguishable from plain chat calls.
// Registered as a processor ahead of the OTLP exporter, OnEnd runs before
// the exporter sees the activity. The steps run one after another, so their
// end order is the chain order. The last step is stored on the
// query-processing activity, so concurrent requests never share it.
public sealed class ExecutionGraphLinks : BaseProcessor<Activity>
{
    public const string WorkflowOperationName = "query-processing";
    private const string LastStepProperty = "infra.execution_graph.last_step";

    public override void OnEnd(Activity activity)
    {
        if (!IsStep(activity)) return;

        // Only direct pipeline steps chain: an agent invoked inside another
        // step (a nested tool agent, say) belongs to that step's own graph.
        Activity? workflow = null;
        for (var parent = activity.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.OperationName == WorkflowOperationName) { workflow = parent; break; }
            if (IsStep(parent)) return;
        }
        if (workflow is null) return;

        if (workflow.GetCustomProperty(LastStepProperty) is ActivityContext previous)
            activity.AddLink(new ActivityLink(previous, new ActivityTagsCollection { ["from"] = "output", ["to"] = "input" }));
        workflow.SetCustomProperty(LastStepProperty, activity.Context);
    }

    private static bool IsStep(Activity activity) =>
        activity.OperationName is "transcribe_audio" or "retrieve_best_practices" ||
        activity.GetTagItem("gen_ai.operation.name") as string == "invoke_agent";
}
