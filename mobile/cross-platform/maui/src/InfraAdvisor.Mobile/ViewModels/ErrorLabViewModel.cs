using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfraAdvisor.Mobile.Observability;
using InfraAdvisor.Mobile.Services;

namespace InfraAdvisor.Mobile.ViewModels;

public partial class ErrorLabViewModel(InfraAdvisorApiClient api, IObservability observability, IAppTerminator appTerminator) : ObservableObject
{
    // Keep the injected process boundary behind a property so Release builds retain a valid constructor while compiling out the destructive Debug-only call.
    private IAppTerminator AppTerminator => appTerminator;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasResult))] private string? resultMessage;
    public bool HasResult => !string.IsNullOrWhiteSpace(ResultMessage);
#if DEBUG
    public bool CrashAvailable => true;
#else
    public bool CrashAvailable => false;
#endif

    [RelayCommand]
    private void SendSampleLogs()
    {
        observability.Info("Error Lab informational sample", new Dictionary<string, object> { ["demo.type"] = "log", ["demo.severity"] = "info" });
        observability.Warning("Error Lab warning sample", new Dictionary<string, object> { ["demo.type"] = "log", ["demo.severity"] = "warning" });
        observability.ErrorLog("Error Lab error sample", new Dictionary<string, object> { ["demo.type"] = "log", ["demo.severity"] = "error" });
        ResultMessage = "Info, warning, and error log samples sent. Open Datadog Logs and filter service:infra-advisor-mobile-maui.";
    }

    [RelayCommand]
    private void TriggerHandledError()
    {
        try
        {
            throw new InvalidOperationException("Intentional handled MAUI demo error");
        }
        catch (InvalidOperationException exception)
        {
            observability.Error("Handled Error Lab exception", exception, new Dictionary<string, object> { ["demo.type"] = "handled_error" });
            ResultMessage = "Handled error recorded. The app remains usable by design.";
        }
    }

    [RelayCommand]
    private async Task TriggerApiErrorAsync()
    {
        try
        {
            await api.TriggerDemoApiErrorAsync();
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            observability.Error("Expected Error Lab API response", exception, new Dictionary<string, object> { ["demo.type"] = "api_error", ["expected"] = true });
            ResultMessage = "Expected API error captured as a correlated RUM resource and error.";
        }
    }

    [RelayCommand]
    private void CrashApp()
    {
#if DEBUG
        AppTerminator.Crash("Intentional InfraAdvisor MAUI Error Lab crash");
#else
        ResultMessage = "Intentional crashes are available only in Debug builds.";
#endif
    }
}
