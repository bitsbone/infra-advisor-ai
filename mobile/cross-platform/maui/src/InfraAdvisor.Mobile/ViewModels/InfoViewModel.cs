using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfraAdvisor.Mobile.Configuration;
using InfraAdvisor.Mobile.Observability;
using InfraAdvisor.Mobile.Services;

namespace InfraAdvisor.Mobile.ViewModels;

public partial class InfoViewModel(AppSession session, AppNavigator navigator, IObservability observability) : ObservableObject
{
    public string Email => session.User?.Email ?? "Not signed in";
    public string UserId => session.User?.Id ?? "—";
    public string ApiBaseUrl => AppConfiguration.ApiBaseUrl;
    public string Site => AppConfiguration.DatadogSite;
    public string Environment => AppConfiguration.DatadogEnvironment;
    public string Service => AppConfiguration.DatadogService;
    public string Sampling => "RUM 100% · Traces 100% · Replay 100%";
    public string ReplayPrivacy => "Mask sensitive inputs";
    public string Version => $"{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";

    [RelayCommand]
    private async Task LogoutAsync()
    {
        observability.Info("Logout started", new Dictionary<string, object> { ["flow"] = "authentication" });
        await session.SignOutAsync();
        observability.ClearUser();
        observability.StopSession();
        navigator.ShowLogin();
    }
}
