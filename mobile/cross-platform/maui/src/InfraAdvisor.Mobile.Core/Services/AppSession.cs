using InfraAdvisor.Mobile.Models;

namespace InfraAdvisor.Mobile.Services;

/// <summary>
/// Holds the active account in memory. The MAUI host may restore this state from platform-protected secure storage; ordinary Preferences must never contain the JWT.
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

    /// <summary>A one-shot navigation handoff from History to the Advisor tab.</summary>
    public string? RequestedConversationId { get; private set; }
    public bool IsNewConversationRequested { get; private set; }

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
        RequestedConversationId = null;
        IsNewConversationRequested = false;
    }

    public void RequestNewConversation()
    {
        StartNewConversation();
        IsNewConversationRequested = true;
    }

    public bool ConsumeNewConversationRequest()
    {
        var value = IsNewConversationRequested;
        IsNewConversationRequested = false;
        return value;
    }

    public void RequestConversation(string conversationId) => RequestedConversationId = conversationId;

    public string? ConsumeRequestedConversation()
    {
        var value = RequestedConversationId;
        RequestedConversationId = null;
        return value;
    }

    public void SignOut()
    {
        ClearAuthentication();
    }

    /// <summary>
    /// Fires when the MAUI host should clear persisted session storage and
    /// return to the login screen after the server rejects the current
    /// token (expired or invalid) — distinct from the user-initiated
    /// <see cref="SignOutAsync"/> flow. Subscribed once at app startup
    /// (see PrismStartup.cs); left null-safe so InfraAdvisorApiClient (a
    /// platform-agnostic Core type) never needs a direct MAUI/navigation
    /// dependency.
    /// </summary>
    public event Func<Task>? SessionExpired;

    public async Task ExpireAsync()
    {
        ClearAuthentication();
        if (SessionExpired is { } handler)
        {
            await handler().ConfigureAwait(false);
        }
    }

    public void RegisterSessionCleanup(Func<Task> cleanup) => sessionCleanup = cleanup;

    public async Task SignOutAsync()
    {
        try
        {
            if (sessionCleanup is { } cleanup)
            {
                await cleanup().ConfigureAwait(false);
            }
        }
        finally
        {
            sessionCleanup = null;
            ClearAuthentication();
        }
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
