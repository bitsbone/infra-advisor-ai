using Datadog.Maui; using InfraAdvisor.Mobile.ViewModels;
namespace InfraAdvisor.Mobile.Views;
[DdView("Profile")]
public partial class InfoPage : ContentPage { public InfoPage(InfoViewModel viewModel) { InitializeComponent(); BindingContext = viewModel; } }
