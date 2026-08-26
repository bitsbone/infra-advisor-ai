using Datadog.Maui;
using InfraAdvisor.Mobile.ViewModels;

namespace InfraAdvisor.Mobile.Views;

[DdView("Chat")]
public partial class ChatPage : ContentPage
{
    public ChatViewModel ViewModel { get; }

    public ChatPage(ChatViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        BindingContext = ViewModel;
        ViewModel.Messages.CollectionChanged += (_, _) =>
        {
            if (ViewModel.Messages.LastOrDefault() is { } latest)
            {
                Dispatcher.Dispatch(() => Transcript.ScrollTo(latest, position: ScrollToPosition.End, animate: true));
            }
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (ViewModel.InitializeCommand.CanExecute(null))
        {
            ViewModel.InitializeCommand.Execute(null);
        }
    }

    private async void OnDeleteConversationClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is not Button { CommandParameter: InfraAdvisor.Mobile.Models.ConversationSummary conversation })
        {
            return;
        }

        var confirmed = await DisplayAlertAsync("Delete conversation?", "This removes the stored conversation and cannot be undone.", "Delete", "Cancel");
        if (confirmed)
        {
            ViewModel.DeleteConversationCommand.Execute(conversation);
        }
    }
}
