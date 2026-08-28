using System.ComponentModel;
using Datadog.Maui;
using InfraAdvisor.Mobile.ViewModels;
using Microsoft.Maui.Accessibility;
namespace InfraAdvisor.Mobile.Views;
[DdView("Errors")]
public partial class ErrorLabPage : ContentPage
{
    private readonly ErrorLabViewModel viewModel;

    public ErrorLabPage(ErrorLabViewModel viewModel)
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
        if (eventArgs.PropertyName == nameof(ErrorLabViewModel.ResultMessage) && viewModel.HasResult)
        {
            Dispatcher.Dispatch(() => SemanticScreenReader.Default.Announce("Diagnostic action complete."));
        }
    }

    private async void OnCrashClicked(object? sender, EventArgs eventArgs)
    {
        var confirmed = await DisplayAlertAsync("Crash InfraAdvisor?", "The process will terminate immediately. Relaunch the app to upload the stored crash report. For iOS, launch without an attached debugger.", "Crash", "Cancel");
        if (confirmed)
        {
            viewModel.CrashAppCommand.Execute(null);
        }
    }
}
