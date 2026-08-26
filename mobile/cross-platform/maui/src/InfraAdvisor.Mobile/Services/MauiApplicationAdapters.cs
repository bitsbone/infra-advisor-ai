using InfraAdvisor.Mobile.Configuration;

namespace InfraAdvisor.Mobile.Services;

/// <summary>
/// Keeps MAUI static platform APIs outside the testable presentation layer.
/// </summary>
public sealed class MauiAppPreferences : IAppPreferences
{
    public string? Get(string key, string? fallback) => Preferences.Default.Get(key, fallback);

    public void Set(string key, string value) => Preferences.Default.Set(key, value);
}

public sealed class MauiClipboardService : IClipboardService
{
    public Task SetTextAsync(string value) => Clipboard.Default.SetTextAsync(value);
}

public sealed class MauiLinkLauncher : ILinkLauncher
{
    public Task OpenAsync(Uri uri) => Launcher.Default.OpenAsync(uri);
}

public sealed class MauiAppRuntimeInfo : IAppRuntimeInfo
{
    public string ApiBaseUrl => AppConfiguration.ApiBaseUrl;
    public string DatadogSite => AppConfiguration.DatadogSite;
    public string DatadogEnvironment => AppConfiguration.DatadogEnvironment;
    public string DatadogService => AppConfiguration.DatadogService;
    public string Version => $"{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";
}

public sealed class MauiAppTerminator : IAppTerminator
{
    public void Crash(string reason) => Environment.FailFast(reason);
}
