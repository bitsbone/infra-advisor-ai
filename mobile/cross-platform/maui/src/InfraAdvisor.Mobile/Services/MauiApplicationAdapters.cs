using InfraAdvisor.Mobile.Configuration;
using InfraAdvisor.Mobile.Models;
using System.Text.Json;

namespace InfraAdvisor.Mobile.Services;

/// <summary>
/// Keeps MAUI static platform APIs outside the testable presentation layer.
/// </summary>
public sealed class MauiAppPreferences : IAppPreferences
{
    public string? Get(string key, string? fallback) => Preferences.Default.Get(key, fallback);

    public void Set(string key, string value) => Preferences.Default.Set(key, value);
}

/// <summary>
/// Stores the JWT and minimum user profile in iOS Keychain or Android encrypted secure storage. The payload is intentionally never emitted to logs or RUM attributes.
/// </summary>
public sealed class MauiSecureSessionStore : ISessionStore
{
    private const string SessionKey = "infra_advisor.auth.session.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public Task SaveAsync(LoginResponse response) => SecureStorage.Default.SetAsync(SessionKey, JsonSerializer.Serialize(response, SerializerOptions));

    public async Task<LoginResponse?> RestoreAsync()
    {
        try
        {
            var value = await SecureStorage.Default.GetAsync(SessionKey);
            return string.IsNullOrWhiteSpace(value) ? null : JsonSerializer.Deserialize<LoginResponse>(value, SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            SecureStorage.Default.Remove(SessionKey);
            return null;
        }
    }

    public Task ClearAsync()
    {
        SecureStorage.Default.Remove(SessionKey);
        return Task.CompletedTask;
    }
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
