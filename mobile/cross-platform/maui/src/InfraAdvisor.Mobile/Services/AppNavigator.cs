using InfraAdvisor.Mobile.Views;
using Prism.Navigation;

namespace InfraAdvisor.Mobile.Services;

/// <summary>
/// Keeps Prism navigation details out of presentation view models. The adapter is page-scoped by Prism,
/// so each operation uses the navigation service associated with the page that initiated it.
/// </summary>
public sealed class AppNavigator(INavigationService navigation) : IAppNavigator
{
    public async Task ShowAuthenticatedAppAsync()
    {
        await NavigateAuthenticatedAsync(navigation);
    }

    public static async Task NavigateAuthenticatedAsync(INavigationService navigationService)
    {
        var result = await navigationService.CreateBuilder()
            .UseAbsoluteNavigation()
            .AddTabbedSegment(tabs => tabs
                .CreateTab(nameof(ChatPage))
                .CreateTab(nameof(HistoryPage))
                .CreateTab(nameof(ErrorLabPage))
                .CreateTab(nameof(InfoPage))
                .SelectedTab(nameof(ChatPage)))
            .NavigateAsync();

        EnsureSucceeded(result, "open the authenticated application");
    }

    public async Task ShowLoginAsync()
    {
        var result = await navigation.NavigateAsync($"/{nameof(LoginPage)}");
        EnsureSucceeded(result, "return to sign in");
    }

    public async Task ShowAdvisorAsync()
    {
        var result = await navigation.SelectTabAsync(nameof(ChatPage));
        EnsureSucceeded(result, "open Chat");
    }

    public static void EnsureSucceeded(INavigationResult result, string operation)
    {
        if (!result.Success && !result.Cancelled)
        {
            throw new InvalidOperationException($"Prism could not {operation}.", result.Exception);
        }
    }
}
