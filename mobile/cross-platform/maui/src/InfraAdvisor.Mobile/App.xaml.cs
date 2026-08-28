using InfraAdvisor.Mobile.Observability;

namespace InfraAdvisor.Mobile;

public partial class App : Application
{
    public App(IObservability observability)
    {
        InitializeComponent();
        observability.Info("InfraAdvisor MAUI application started", new Dictionary<string, object> { ["app.version"] = AppInfo.Current.VersionString, ["platform"] = DeviceInfo.Current.Platform.ToString() });
    }
}
