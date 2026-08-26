using Datadog.Maui; using InfraAdvisor.Mobile.ViewModels;
namespace InfraAdvisor.Mobile.Views;
[DdView("Chat")]
public partial class ChatPage : ContentPage
{
    public ChatPage(ChatViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is ChatViewModel viewModel && viewModel.InitializeCommand.CanExecute(null))
        {
            viewModel.InitializeCommand.Execute(null);
        }
    }

    private async void OnDeleteConversationClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is not Button { CommandParameter: InfraAdvisor.Mobile.Models.ConversationSummary conversation } || BindingContext is not ChatViewModel viewModel)
        {
            return;
        }

        var confirmed = await DisplayAlertAsync("Delete conversation?", "This removes the stored conversation and cannot be undone.", "Delete", "Cancel");
        if (confirmed)
        {
            viewModel.DeleteConversationCommand.Execute(conversation);
        }
    }
}
