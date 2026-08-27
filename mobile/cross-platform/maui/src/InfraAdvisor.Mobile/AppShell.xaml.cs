namespace InfraAdvisor.Mobile;

public partial class AppShell : Shell
{
    public AppShell(IServiceProvider services)
    {
        InitializeComponent();
        Items.Add(new TabBar
        {
            Items =
            {
                new ShellContent { Title = "Advisor", Route = "chat", Content = services.GetRequiredService<Views.ChatPage>() },
                new ShellContent { Title = "History", Route = "history", Content = services.GetRequiredService<Views.HistoryPage>() },
                new ShellContent { Title = "Diagnostics", Route = "errors", Content = services.GetRequiredService<Views.ErrorLabPage>() },
                new ShellContent { Title = "Profile", Route = "info", Content = services.GetRequiredService<Views.InfoPage>() },
            },
        });
    }
}
