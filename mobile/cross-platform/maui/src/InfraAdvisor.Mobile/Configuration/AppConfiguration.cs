using System.Reflection;

namespace InfraAdvisor.Mobile.Configuration;

/// <summary>
/// Public mobile configuration only. Datadog client tokens and RUM application IDs identify a client build and are safe to distribute; API/application keys must remain in the build environment.
/// </summary>
public static class AppConfiguration
{
    public static string ApiBaseUrl => Get("InfraAdvisorApiBaseUrl", "https://infra-advisor-ai.bitsbone.com/");
    public static string ApiFirstPartyHost
    {
        get
        {
            if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new InvalidOperationException("InfraAdvisorApiBaseUrl must be an absolute HTTP or HTTPS URL.");
            }

            return uri.Host;
        }
    }
    public const string DatadogSite = "US3";
    public static string DatadogEnvironment => Get("InfraAdvisorDatadogEnvironment", "demo");
    public static string DatadogService => Get("InfraAdvisorDatadogService", "infra-advisor-mobile-maui");
    public static string DatadogClientToken => Get("InfraAdvisorDatadogClientToken", "pub884d0800477e2d252b992acb168fc7a5");
    public static string DatadogRumApplicationId => Get("InfraAdvisorDatadogRumApplicationId", "fe90f908-da00-4d7c-9b24-6af11cee68a4");
    public const double SessionSampleRate = 100.0;
    public const double ResourceTraceSampleRate = 100.0;
    public const double ReplaySampleRate = 100.0;

    private static string Get(string key, string fallback) => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value ?? fallback;
}
