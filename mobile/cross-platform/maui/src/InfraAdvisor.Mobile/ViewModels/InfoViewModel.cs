using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfraAdvisor.Mobile.Observability;
using InfraAdvisor.Mobile.Services;

namespace InfraAdvisor.Mobile.ViewModels;

public partial class InfoViewModel(AppSession session, ISessionStore sessionStore, IAppNavigator navigator, IObservability observability, IAppRuntimeInfo runtimeInfo) : ObservableObject
{
    public string Email => session.User?.Email ?? "Not signed in";
    public string UserId => session.User?.Id ?? "—";
    public string ApiBaseUrl => runtimeInfo.ApiBaseUrl;
    public string Site => runtimeInfo.DatadogSite;
    public string Environment => runtimeInfo.DatadogEnvironment;
    public string Service => runtimeInfo.DatadogService;
    public string Sampling => "RUM 100% · Traces 100% · Replay 100%";
    public string ReplayPrivacy => "Mask sensitive inputs";
    public string Version => runtimeInfo.Version;

    [RelayCommand]
    private async Task LogoutAsync()
    {
        var operationKey = observability.StartOperation("authentication.logout", new Dictionary<string, object> { ["flow"] = "authentication" });
        observability.Info("Logout started", new Dictionary<string, object> { ["flow"] = "authentication" });
        try
        {
            await session.SignOutAsync();
            observability.SucceedOperation("authentication.logout", operationKey, new Dictionary<string, object> { ["result"] = "success" });
        }
        catch (Exception exception)
        {
            observability.FailOperation("authentication.logout", operationKey, abandoned: false, new Dictionary<string, object> { ["result"] = "cleanup_error" });
            observability.Error("Logout cleanup failed", exception);
        }
        finally
        {
            await sessionStore.ClearAsync();
            observability.ClearUser();
            observability.StopSession();
            await navigator.ShowLoginAsync();
        }
    }
}
