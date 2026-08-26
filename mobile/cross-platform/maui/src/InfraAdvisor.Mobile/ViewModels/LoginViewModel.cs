using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfraAdvisor.Mobile.Observability;
using InfraAdvisor.Mobile.Services;

namespace InfraAdvisor.Mobile.ViewModels;

public partial class LoginViewModel(InfraAdvisorApiClient api, AppSession session, AppNavigator navigator, IObservability observability) : ObservableObject
{
    [ObservableProperty] private string email = string.Empty;
    [ObservableProperty] private string password = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSubmit)), NotifyPropertyChangedFor(nameof(SubmitLabel))] private bool isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? errorMessage;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool CanSubmit => !IsBusy && !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);
    public string SubmitLabel => IsBusy ? "Signing in…" : "Sign in";

    partial void OnEmailChanged(string value) => OnPropertyChanged(nameof(CanSubmit));
    partial void OnPasswordChanged(string value) => OnPropertyChanged(nameof(CanSubmit));

    [RelayCommand]
    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        if (!CanSubmit)
        {
            ErrorMessage = "Enter both an email address and password.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        observability.Info("Login started", new Dictionary<string, object> { ["flow"] = "authentication" });
        try
        {
            var response = await api.LoginAsync(Email.Trim(), Password, cancellationToken);
            session.SignIn(response);
            Password = string.Empty;
            observability.IdentifyUser(response.User.Id, response.User.Email);
            observability.Info("Login completed", new Dictionary<string, object> { ["result"] = "success" });
            navigator.ShowAuthenticatedApp();
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Sign in was canceled.";
        }
        catch (ApiException exception)
        {
            ErrorMessage = exception.Message;
            observability.Error("Login failed", exception, new Dictionary<string, object> { ["status_code"] = exception.StatusCode ?? 0 });
        }
        catch (HttpRequestException exception)
        {
            ErrorMessage = "The service could not be reached. Check your connection and try again.";
            observability.Error("Login transport failed", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
