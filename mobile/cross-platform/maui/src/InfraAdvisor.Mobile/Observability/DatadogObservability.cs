using Datadog.Maui;
using Datadog.Maui.Configuration;
using InfraAdvisor.Mobile.Services;

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

    public string StartOperation(string name, IReadOnlyDictionary<string, object>? attributes = null)
    {
        var operationKey = Guid.NewGuid().ToString("N");
        DdRum.StartOperation(name, operationKey, TelemetrySanitizer.FilterAttributes(attributes));
        return operationKey;
    }

    public void SucceedOperation(string name, string operationKey, IReadOnlyDictionary<string, object>? attributes = null) =>
        DdRum.SucceedOperation(name, operationKey, TelemetrySanitizer.FilterAttributes(attributes));

    public void FailOperation(string name, string operationKey, bool abandoned, IReadOnlyDictionary<string, object>? attributes = null) =>
        DdRum.FailOperation(name, abandoned ? OperationFailure.Abandoned : OperationFailure.Error, operationKey, TelemetrySanitizer.FilterAttributes(attributes));

    public void Info(string message, IReadOnlyDictionary<string, object>? attributes = null)
    {
        DdLogs.LogWithAttributes("info", message, TelemetrySanitizer.FilterAttributes(attributes));
    }

    public void Error(string message, Exception exception, IReadOnlyDictionary<string, object>? attributes = null)
    {
        var safeAttributes = TelemetrySanitizer.FilterAttributes(attributes);
        safeAttributes["error.type"] = exception.GetType().Name;
        DdLogs.LogWithAttributes("error", $"{message} ({exception.GetType().Name})", safeAttributes);
        var safeStacktrace = $"{exception.GetType().FullName}\n{TelemetrySanitizer.SanitizeDiagnosticText(exception.StackTrace)}";
        DdRum.AddError(message, RumErrorSource.Source, safeStacktrace, safeAttributes, 0, string.Empty);
    }
}
