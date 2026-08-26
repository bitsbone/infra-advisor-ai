using Datadog.Maui;
using Datadog.Maui.Configuration;

namespace InfraAdvisor.Mobile.Observability;

/// <summary>
/// Keeps telemetry calls explicit and reviewable. Callers may supply operational state, never prompts, response bodies, tokens, email addresses, filenames, paths, or attachment URLs.
/// </summary>
public sealed class DatadogObservability : IObservability
{
    public void IdentifyUser(string id, string email)
    {
        DdSdk.SetUserInfo(id, name: null, email);
        DdLogs.Info("Authenticated mobile session associated with a user");
    }

    public void ClearUser()
    {
        DdSdk.ClearUserInfo();
        DdLogs.Info("Mobile user session cleared");
    }

    public void StopSession() => DdRum.StopSession();

    public void Info(string message, IReadOnlyDictionary<string, object>? attributes = null) => DdLogs.Info(message);

    public void Error(string message, Exception exception, IReadOnlyDictionary<string, object>? attributes = null)
    {
        var safeAttributes = attributes is null ? new Dictionary<string, object>() : new Dictionary<string, object>(attributes);
        safeAttributes["error.type"] = exception.GetType().Name;
        DdLogs.Error($"{message} ({exception.GetType().Name})");
        DdRum.AddError(message, RumErrorSource.Source, exception.ToString(), safeAttributes, 0, string.Empty);
    }
}
