using Datadog.Maui; using InfraAdvisor.Mobile.ViewModels;
namespace InfraAdvisor.Mobile.Views;
[DdView("Errors")]
public partial class ErrorLabPage : ContentPage
{
    public ErrorLabPage(ErrorLabViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnCrashClicked(object? sender, EventArgs eventArgs)
    {
        var confirmed = await DisplayAlertAsync("Crash Infra Advisor?", "The process will terminate immediately. Relaunch the app to upload the stored crash report. For iOS, launch without an attached debugger.", "Crash", "Cancel");
        if (confirmed && BindingContext is ErrorLabViewModel viewModel)
        {
            viewModel.CrashAppCommand.Execute(null);
        }
    }
}
