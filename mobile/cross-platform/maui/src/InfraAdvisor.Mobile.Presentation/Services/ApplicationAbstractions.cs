namespace InfraAdvisor.Mobile.Services;

public interface IAppNavigator
{
    void ShowAuthenticatedApp();

    void ShowLogin();

    Task ShowAdvisorAsync();
}

public interface IAppPreferences
{
    string? Get(string key, string? fallback);

    void Set(string key, string value);
}

public interface IClipboardService
{
    Task SetTextAsync(string value);
}

public interface ILinkLauncher
{
    Task OpenAsync(Uri uri);
}

public interface IAppRuntimeInfo
{
    string ApiBaseUrl { get; }

    string DatadogSite { get; }

    string DatadogEnvironment { get; }

    string DatadogService { get; }

    string Version { get; }
}

public interface IAppTerminator
{
    void Crash(string reason);
}
