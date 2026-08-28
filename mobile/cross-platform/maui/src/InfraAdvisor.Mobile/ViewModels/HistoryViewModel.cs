using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfraAdvisor.Mobile.Models;
using InfraAdvisor.Mobile.Observability;
using InfraAdvisor.Mobile.Services;

namespace InfraAdvisor.Mobile.ViewModels;

public partial class HistoryViewModel(InfraAdvisorApiClient api, AppSession session, IAppNavigator navigator, IObservability observability) : ObservableObject
{
    public ObservableCollection<ConversationSummary> Conversations { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? errorMessage;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNoConversations)), NotifyPropertyChangedFor(nameof(CanRefresh))] private bool isLoading;
    [ObservableProperty] private ConversationSummary? selectedConversation;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasNoConversations => !IsLoading && Conversations.Count == 0;
    public bool CanRefresh => !IsLoading;

    partial void OnSelectedConversationChanged(ConversationSummary? value)
    {
        if (value is not null)
        {
            _ = OpenConversationAsync(value);
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var conversations = await api.GetConversationsAsync();
            Conversations.Clear();
            foreach (var conversation in conversations)
            {
                Conversations.Add(conversation);
            }
            OnPropertyChanged(nameof(HasNoConversations));
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            ErrorMessage = "Conversation history is temporarily unavailable.";
            observability.Error("Conversation history load failed", exception, new Dictionary<string, object> { ["screen"] = "history" });
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task NewConversationAsync()
    {
        session.RequestNewConversation();
        await navigator.ShowAdvisorAsync();
    }

    private async Task OpenConversationAsync(ConversationSummary conversation)
    {
        session.RequestConversation(conversation.Id);
        observability.Info("Conversation selected", new Dictionary<string, object> { ["screen"] = "history", ["backend"] = conversation.Backend ?? "unknown" });
        SelectedConversation = null;
        await navigator.ShowAdvisorAsync();
    }
}
