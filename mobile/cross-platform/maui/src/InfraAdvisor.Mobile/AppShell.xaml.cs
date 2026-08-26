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
                new ShellContent { Title = "Chat", Route = "chat", Content = services.GetRequiredService<Views.ChatPage>() },
                new ShellContent { Title = "Errors", Route = "errors", Content = services.GetRequiredService<Views.ErrorLabPage>() },
                new ShellContent { Title = "Info", Route = "info", Content = services.GetRequiredService<Views.InfoPage>() },
            },
        });
    }
}
