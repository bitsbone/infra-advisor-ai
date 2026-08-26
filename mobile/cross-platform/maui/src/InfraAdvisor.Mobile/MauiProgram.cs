using Datadog.Maui.Configuration;
using Datadog.Maui.Hosting;
using InfraAdvisor.Mobile.Configuration;
using InfraAdvisor.Mobile.Observability;
using InfraAdvisor.Mobile.Services;
using InfraAdvisor.Mobile.Services.Media;
using InfraAdvisor.Mobile.ViewModels;
using InfraAdvisor.Mobile.Views;
using Plugin.Maui.Audio;
using Syncfusion.Maui.Toolkit.Hosting;

namespace InfraAdvisor.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureSyncfusionToolkit()
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
                        Match = "infra-advisor-ai.kyletaylor.dev",
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
        builder.Services.AddSingleton<AppNavigator>();
        builder.Services.AddSingleton<IAppNavigator>(services => services.GetRequiredService<AppNavigator>());
        builder.Services.AddSingleton<IAppPreferences, MauiAppPreferences>();
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

        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<ChatViewModel>();
        builder.Services.AddTransient<ErrorLabViewModel>();
        builder.Services.AddTransient<InfoViewModel>();
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<ChatPage>();
        builder.Services.AddTransient<ErrorLabPage>();
        builder.Services.AddTransient<InfoPage>();
        builder.Services.AddTransient<AppShell>();

        return builder.Build();
    }
}
