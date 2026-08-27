using System.ComponentModel;
using Datadog.Maui;
using InfraAdvisor.Mobile.ViewModels;
using Microsoft.Maui.Accessibility;

namespace InfraAdvisor.Mobile.Views;

[DdView("History")]
public partial class HistoryPage : ContentPage
{
    private readonly HistoryViewModel viewModel;

    public HistoryPage(HistoryViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        if (viewModel.LoadCommand.CanExecute(null))
        {
            viewModel.LoadCommand.Execute(null);
        }
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
            nameof(HistoryViewModel.IsLoading) when viewModel.IsLoading => "Loading conversation history.",
            nameof(HistoryViewModel.IsLoading) when !viewModel.IsLoading && !viewModel.HasError => $"Conversation history loaded with {viewModel.Conversations.Count} items.",
            nameof(HistoryViewModel.HasError) when viewModel.HasError => "Conversation history could not be loaded.",
            _ => null,
        };
        if (announcement is not null)
        {
            Dispatcher.Dispatch(() => SemanticScreenReader.Default.Announce(announcement));
        }
    }
}
