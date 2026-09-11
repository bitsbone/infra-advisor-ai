using System.Threading;

namespace InfraAdvisor.AgentApi.Observability;

// Makes the current request's OpenFeature targeting context (user id +
// job_role, see PromptVersionFlags/SpecialistRegistry.GetWorkflowForRequestAsync)
// visible to the ActivityListener in Program.cs that tags invoke_agent/chat
// spans with the resolved prompt.version — same AsyncLocal-stash pattern as
// AmbientSessionContext, for the same reason: the listener callback has no
// per-request context of its own, and MAF's own OTel decorators emit these
// spans without a call site to pass this through directly.
//
// Without this, a trace shows WHICH prompt version answered a turn but not
// WHAT targeting attribute value caused that resolution — e.g. confirming
// "job_role=Civil Engineer" is actually why a pinned version was served,
// rather than just trusting the admin UI's current setting for that user.
public static class AmbientTargetingContext
{
    private static readonly AsyncLocal<string?> _targetingKey = new();
    private static readonly AsyncLocal<string?> _jobRole = new();

    public static string? TargetingKey => _targetingKey.Value;
    public static string? JobRole => _jobRole.Value;

    public static void Set(string? targetingKey, string? jobRole)
    {
        _targetingKey.Value = targetingKey;
        _jobRole.Value = jobRole;
    }
}
