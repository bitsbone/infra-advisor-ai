using System.ComponentModel;
using Datadog.Maui;
using InfraAdvisor.Mobile.ViewModels;
using Microsoft.Maui.Accessibility;

namespace InfraAdvisor.Mobile.Views;

[DdView("Advisor")]
public partial class ChatPage : ContentPage
{
    private WeakReference<Button>? evidenceInvoker;

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
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        if (ViewModel.InitializeCommand.CanExecute(null))
        {
            ViewModel.InitializeCommand.Execute(null);
        }
    }

    protected override void OnDisappearing()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnDisappearing();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ChatViewModel.IsHistoryVisible))
        {
            Dispatcher.Dispatch(() => ((VisualElement)(ViewModel.IsHistoryVisible ? CloseConversationHistoryButton : CompactHistoryButton)).Focus());
        }
        else if (eventArgs.PropertyName == nameof(ChatViewModel.IsEvidenceVisible))
        {
            Dispatcher.Dispatch(() =>
            {
                if (ViewModel.IsEvidenceVisible)
                {
                    CloseEvidenceButton.Focus();
                }
                else if (evidenceInvoker?.TryGetTarget(out var invoker) == true && invoker.IsVisible)
                {
                    invoker.Focus();
                }
                else
                {
                    Transcript.Focus();
                }
            });
        }

        var announcement = eventArgs.PropertyName switch
        {
            nameof(ChatViewModel.IsBusy) when ViewModel.IsBusy => "Infra Advisor is working.",
            nameof(ChatViewModel.IsBusy) when !ViewModel.IsBusy && ViewModel.HasError => "The advisor request needs attention.",
            nameof(ChatViewModel.IsBusy) when !ViewModel.IsBusy => "Infra Advisor response complete.",
            nameof(ChatViewModel.IsStillWorking) when ViewModel.IsStillWorking => "Infra Advisor is still working.",
            nameof(ChatViewModel.IsEvidenceVisible) when ViewModel.IsEvidenceVisible => $"Evidence panel opened with {ViewModel.SelectedEvidenceMessage?.Evidence.Count ?? 0} items.",
            nameof(ChatViewModel.IsEvidenceVisible) => "Evidence panel closed.",
            nameof(ChatViewModel.IsHistoryVisible) when ViewModel.IsHistoryVisible => "Conversation history opened.",
            nameof(ChatViewModel.IsHistoryVisible) => "Conversation history closed.",
            nameof(ChatViewModel.IsRecording) when ViewModel.IsRecording => "Audio recording started.",
            nameof(ChatViewModel.IsRecording) => "Audio recording stopped.",
            _ => null,
        };
        if (announcement is not null)
        {
            Dispatcher.Dispatch(() => SemanticScreenReader.Default.Announce(announcement));
        }
    }

    private void OnOpenEvidenceClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is Button button)
        {
            evidenceInvoker = new WeakReference<Button>(button);
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
