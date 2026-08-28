using System.ComponentModel;
using System.Collections.Specialized;
using Datadog.Maui;
using InfraAdvisor.Mobile.Models;
using InfraAdvisor.Mobile.ViewModels;
using Microsoft.Maui.Accessibility;

namespace InfraAdvisor.Mobile.Views;

[DdView("Chat")]
public partial class ChatPage : ContentPage
{
    private WeakReference<Button>? evidenceInvoker;

    public ChatViewModel ViewModel { get; }

    public ChatPage(ChatViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        BindingContext = ViewModel;
        ViewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.OldItems is not null)
        {
            foreach (ChatMessageItem message in eventArgs.OldItems)
            {
                message.PropertyChanged -= OnMessagePropertyChanged;
            }
        }
        if (eventArgs.NewItems is not null)
        {
            foreach (ChatMessageItem message in eventArgs.NewItems)
            {
                message.PropertyChanged += OnMessagePropertyChanged;
            }
        }

        if (ViewModel.Messages.LastOrDefault() is { } latest)
        {
            Dispatcher.Dispatch(() => Transcript.ScrollTo(latest, position: ScrollToPosition.End, animate: true));
        }
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ChatMessageItem.ActionStatus) && sender is ChatMessageItem { ActionStatus: { Length: > 0 } status })
        {
            Dispatcher.Dispatch(() => SemanticScreenReader.Default.Announce(status));
        }
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
        if (eventArgs.PropertyName == nameof(ChatViewModel.IsSettingsVisible))
        {
            Dispatcher.Dispatch(() => ((VisualElement)(ViewModel.IsSettingsVisible ? CloseResponseSettingsButton : Transcript)).Focus());
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
            nameof(ChatViewModel.IsBusy) when ViewModel.IsBusy => "InfraAdvisor is working.",
            nameof(ChatViewModel.IsBusy) when !ViewModel.IsBusy && ViewModel.HasError => "The advisor request needs attention.",
            nameof(ChatViewModel.IsBusy) when !ViewModel.IsBusy => "InfraAdvisor response complete.",
            nameof(ChatViewModel.IsStillWorking) when ViewModel.IsStillWorking => "InfraAdvisor is still working.",
            nameof(ChatViewModel.IsEvidenceVisible) when ViewModel.IsEvidenceVisible => $"Evidence panel opened with {ViewModel.SelectedEvidenceMessage?.Evidence.Count ?? 0} items.",
            nameof(ChatViewModel.IsEvidenceVisible) => "Evidence panel closed.",
            nameof(ChatViewModel.IsSettingsVisible) when ViewModel.IsSettingsVisible => "Response settings opened.",
            nameof(ChatViewModel.IsSettingsVisible) => "Response settings closed.",
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

}
