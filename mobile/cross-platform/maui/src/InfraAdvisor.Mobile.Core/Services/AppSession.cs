using InfraAdvisor.Mobile.Models;

namespace InfraAdvisor.Mobile.Services;

/// <summary>
/// Holds account credentials only for the process lifetime. JWTs are intentionally never written to Preferences or secure storage in this educational demo.
/// </summary>
public sealed class AppSession
{
    private Func<Task>? sessionCleanup;
    public string? Token { get; private set; }

    public User? User { get; private set; }

    public BackendKind Backend { get; set; } = BackendKind.Python;

    public string? Model { get; set; }

    public string SessionId { get; private set; } = Guid.NewGuid().ToString();

    public string? ConversationId { get; set; }

    public bool IsAuthenticated => Token is not null && User is not null;

    public void SignIn(LoginResponse response)
    {
        Token = response.Token;
        User = response.User;
        StartNewConversation();
    }

    public void StartNewConversation()
    {
        SessionId = Guid.NewGuid().ToString();
        ConversationId = null;
    }

    public void SignOut()
    {
        ClearAuthentication();
    }

    public void RegisterSessionCleanup(Func<Task> cleanup) => sessionCleanup = cleanup;

    public async Task SignOutAsync()
    {
        if (sessionCleanup is { } cleanup)
        {
            await cleanup().ConfigureAwait(false);
            sessionCleanup = null;
        }

        ClearAuthentication();
    }

    private void ClearAuthentication()
    {
        Token = null;
        User = null;
        Model = null;
        StartNewConversation();
    }
}

public interface IRumSessionProvider
{
    string? CurrentSessionId { get; }
}

public sealed class EmptyRumSessionProvider : IRumSessionProvider
{
    public string? CurrentSessionId => null;
}
