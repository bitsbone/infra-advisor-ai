using InfraAdvisor.Mobile.Views;
using InfraAdvisor.Mobile.Observability;

namespace InfraAdvisor.Mobile;

public partial class App : Application
{
    private readonly IServiceProvider services;

    public App(IServiceProvider services, IObservability observability)
    {
        InitializeComponent();
        this.services = services;
        observability.Info("Infra Advisor MAUI application started", new Dictionary<string, object> { ["app.version"] = AppInfo.Current.VersionString, ["platform"] = DeviceInfo.Current.Platform.ToString() });
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
#if DEBUG
        // Resolve the authenticated shell while app resources are available so invalid XAML or missing DI registrations fail during development before a successful login changes the root page.
        _ = services.GetRequiredService<AppShell>();
#endif
        return new Window(services.GetRequiredService<LoginPage>());
    }
}
