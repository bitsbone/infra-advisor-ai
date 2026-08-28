namespace InfraAdvisor.Mobile.Services;

using InfraAdvisor.Mobile.Models;

public interface IAppNavigator
{
    Task ShowAuthenticatedAppAsync();

    Task ShowLoginAsync();

    Task ShowAdvisorAsync();
}

public interface IAppPreferences
{
    string? Get(string key, string? fallback);

    void Set(string key, string value);
}

/// <summary>
/// Persists only the authenticated session in platform-protected storage. Implementations must never use plain preferences or log the serialized value.
/// </summary>
public interface ISessionStore
{
    Task SaveAsync(LoginResponse response);

    Task<LoginResponse?> RestoreAsync();

    Task ClearAsync();
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
