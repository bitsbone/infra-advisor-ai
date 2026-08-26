namespace InfraAdvisor.Mobile.Services;

public sealed class AppNavigator(IServiceProvider services)
{
    public void ShowAuthenticatedApp()
    {
        if (Application.Current?.Windows.FirstOrDefault() is { } window)
        {
            window.Page = services.GetRequiredService<AppShell>();
        }
    }

    public void ShowLogin()
    {
        if (Application.Current?.Windows.FirstOrDefault() is { } window)
        {
            window.Page = services.GetRequiredService<Views.LoginPage>();
        }
    }
}
