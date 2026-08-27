using System.ComponentModel;
using Datadog.Maui;
using InfraAdvisor.Mobile.ViewModels;
using Microsoft.Maui.Accessibility;
namespace InfraAdvisor.Mobile.Views;
[DdView("Login")]
public partial class LoginPage : ContentPage
{
    private readonly LoginViewModel viewModel;

    public LoginPage(LoginViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnDisappearing()
    {
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnDisappearing();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        var announcement = eventArgs.PropertyName switch
        {
            nameof(LoginViewModel.IsBusy) when viewModel.IsBusy => "Signing in.",
            nameof(LoginViewModel.HasError) when viewModel.HasError => "Sign in failed. Review the message and try again.",
            _ => null,
        };
        if (announcement is not null)
        {
            Dispatcher.Dispatch(() => SemanticScreenReader.Default.Announce(announcement));
        }
    }

    private void OnEmailCompleted(object? sender, EventArgs eventArgs) => PasswordEntry.Focus();

    private void OnPasswordCompleted(object? sender, EventArgs eventArgs)
    {
        if (viewModel.LoginCommand.CanExecute(null))
        {
            viewModel.LoginCommand.Execute(null);
        }
    }
}
