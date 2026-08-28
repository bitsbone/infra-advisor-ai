using Datadog.Maui.Configuration;
using Datadog.Maui.Hosting;
using InfraAdvisor.Mobile.Configuration;
using InfraAdvisor.Mobile.Observability;
using InfraAdvisor.Mobile.Services;
using InfraAdvisor.Mobile.Services.Media;
using InfraAdvisor.Mobile.ViewModels;
using InfraAdvisor.Mobile.Views;
using Plugin.Maui.Audio;

namespace InfraAdvisor.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UsePrism(PrismStartup.Configure)
            .UseDatadog(new DdSdkConfiguration
            {
                ClientToken = AppConfiguration.DatadogClientToken,
                Environment = AppConfiguration.DatadogEnvironment,
                TrackingConsent = TrackingConsent.Granted,
                Service = AppConfiguration.DatadogService,
                Site = DatadogSite.Us3,
                NativeCrashReportEnabled = true,
                FirstPartyHosts =
                [
                    new FirstPartyHost
                    {
                        // Keep propagation aligned with the same build-time API
                        // override used by HttpClient. An alternate demo host
                        // must not silently lose mobile-to-backend trace context.
                        Match = AppConfiguration.ApiFirstPartyHost,
                        HeaderTypes = [TracingHeaderType.Datadog, TracingHeaderType.TraceContext],
                    },
                ],
            })
            .UseDatadogLogs()
            .UseDatadogTrace()
            .UseDatadogRum(new DdRumConfiguration
            {
                ApplicationId = AppConfiguration.DatadogRumApplicationId,
                SessionSampleRate = AppConfiguration.SessionSampleRate,
                ResourceTraceSampleRate = AppConfiguration.ResourceTraceSampleRate,
                TrackFrustrations = true,
                TrackBackgroundEvents = true,
                AutomaticViewTracking = true,
                AutomaticActionTracking = true,
                AutomaticResourceTracking = true,
                ResourceEventMapper = resource =>
                {
                    resource.Url = TelemetrySanitizer.SanitizeUrl(resource.Url);
                    resource.Context = TelemetrySanitizer.FilterAttributes(resource.Context);
                    return resource;
                },
                ActionEventMapper = action =>
                {
                    action.Name = TelemetrySanitizer.SanitizeActionName(action.Name);
                    action.Context = TelemetrySanitizer.FilterAttributes(action.Context);
                    return action;
                },
                ErrorEventMapper = error =>
                {
                    error.Stacktrace = TelemetrySanitizer.SanitizeDiagnosticText(error.Stacktrace);
                    error.Context = TelemetrySanitizer.FilterAttributes(error.Context);
                    return error;
                },
            })
            .UseDatadogSessionReplay(new SessionReplayConfiguration
            {
                ReplaySampleRate = AppConfiguration.ReplaySampleRate,
                TextAndInputPrivacyLevel = TextAndInputPrivacy.MaskSensitiveInputs,
            });

        builder.Services.AddSingleton(new AppSession());
        builder.Services.AddSingleton(AudioManager.Current);
        builder.Services.AddSingleton<IMediaInputService, MediaInputService>();
        builder.Services.AddSingleton<IRumSessionProvider, MauiRumSessionProvider>();
        builder.Services.AddSingleton<IObservability, DatadogObservability>();
        builder.Services.AddSingleton<IAppPreferences, MauiAppPreferences>();
        builder.Services.AddSingleton<ISessionStore, MauiSecureSessionStore>();
        builder.Services.AddSingleton<IClipboardService, MauiClipboardService>();
        builder.Services.AddSingleton<ILinkLauncher, MauiLinkLauncher>();
        builder.Services.AddSingleton<IAppRuntimeInfo, MauiAppRuntimeInfo>();
        builder.Services.AddSingleton<IAppTerminator, MauiAppTerminator>();
        builder.Services.AddHttpClient<InfraAdvisorApiClient>(client =>
        {
            client.BaseAddress = new Uri(AppConfiguration.ApiBaseUrl);
            client.Timeout = TimeSpan.FromMinutes(3);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("InfraAdvisor-MAUI/0.1.0");
        });

        return builder.Build();
    }
}
