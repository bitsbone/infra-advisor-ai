using InfraAdvisor.Mobile.Views;
using InfraAdvisor.Mobile.Observability;

namespace InfraAdvisor.Mobile;

public partial class App : Application
{
    private readonly LoginPage loginPage;

    public App(LoginPage loginPage, IObservability observability)
    {
        InitializeComponent();
        this.loginPage = loginPage;
        observability.Info("Infra Advisor MAUI application started", new Dictionary<string, object> { ["app.version"] = AppInfo.Current.VersionString, ["platform"] = DeviceInfo.Current.Platform.ToString() });
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(loginPage);
}
