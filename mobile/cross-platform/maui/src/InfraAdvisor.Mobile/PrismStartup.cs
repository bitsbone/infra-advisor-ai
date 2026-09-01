using InfraAdvisor.Mobile.Observability;
using InfraAdvisor.Mobile.Services;
using InfraAdvisor.Mobile.ViewModels;
using InfraAdvisor.Mobile.Views;
using Prism.Events;
using Prism.Ioc;
using Prism.Navigation;

namespace InfraAdvisor.Mobile;

/// <summary>
/// Central Prism composition root. Visual pages are registered for navigation here while infrastructure
/// services remain in <see cref="MauiProgram"/> so the integration pattern stays easy to locate and reuse.
/// </summary>
public static class PrismStartup
{
    public static void Configure(PrismAppBuilder prism)
    {
        prism.RegisterTypes(RegisterNavigation)
            .OnInitialized(ObserveNavigation)
            .CreateWindow(async (container, navigation) =>
            {
                var store = container.Resolve<ISessionStore>();
                var session = container.Resolve<AppSession>();
                var observability = container.Resolve<IObservability>();

                // Any 401 (expired/invalid token) triggers AppSession.ExpireAsync(),
                // which raises this event — mirrors InfoViewModel.LogoutAsync()'s
                // manual-logout teardown, but server-driven rather than user-initiated.
                session.SessionExpired += async () =>
                {
                    await store.ClearAsync();
                    observability.ClearUser();
                    observability.StopSession();
                    var expiredResult = await navigation.NavigateAsync($"/{nameof(LoginPage)}");
                    AppNavigator.EnsureSucceeded(expiredResult, "return to sign in after session expiry");
                };

                try
                {
                    var restored = await store.RestoreAsync();
                    if (restored is not null)
                    {
                        session.SignIn(restored);
                        observability.IdentifyUser(restored.User.Id, restored.User.Email);
                        await AppNavigator.NavigateAuthenticatedAsync(navigation);
                        observability.Info("Saved mobile session restored", new Dictionary<string, object> { ["flow"] = "authentication" });
                        return;
                    }
                }
                catch (Exception exception)
                {
                    await store.ClearAsync();
                    session.SignOut();
                    observability.Warning("Saved mobile session could not be restored", new Dictionary<string, object> { ["error_type"] = exception.GetType().Name });
                }

                var result = await navigation.NavigateAsync($"/{nameof(LoginPage)}");
                AppNavigator.EnsureSucceeded(result, "open sign in");
            });
    }

    private static void RegisterNavigation(IContainerRegistry container)
    {
        container.Register<IAppNavigator, AppNavigator>();
        container.RegisterForNavigation<LoginPage, LoginViewModel>();
        container.RegisterForNavigation<ChatPage, ChatViewModel>();
        container.RegisterForNavigation<HistoryPage, HistoryViewModel>();
        container.RegisterForNavigation<ErrorLabPage, ErrorLabViewModel>();
        container.RegisterForNavigation<InfoPage, InfoViewModel>();
    }

    private static void ObserveNavigation(IContainerProvider container)
    {
        var observability = container.Resolve<IObservability>();
        container.Resolve<IEventAggregator>()
            .GetEvent<NavigationRequestEvent>()
            .Subscribe(context =>
            {
                if (context.Result.Success)
                {
                    observability.Info("Mobile navigation completed", new Dictionary<string, object> { ["navigation.result"] = "success", ["navigation.type"] = context.Type.ToString() });
                }
                else if (!context.Result.Cancelled)
                {
                    observability.Error("Mobile navigation failed", context.Result.Exception ?? new InvalidOperationException("Prism navigation failed without an exception."), new Dictionary<string, object> { ["navigation.result"] = "failure", ["navigation.type"] = context.Type.ToString() });
                }
            });
    }
}
