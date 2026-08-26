using Datadog.Maui;
using InfraAdvisor.Mobile.ViewModels;
namespace InfraAdvisor.Mobile.Views;
[DdView("Login")]
public partial class LoginPage : ContentPage { public LoginPage(LoginViewModel viewModel) { InitializeComponent(); BindingContext = viewModel; } }
