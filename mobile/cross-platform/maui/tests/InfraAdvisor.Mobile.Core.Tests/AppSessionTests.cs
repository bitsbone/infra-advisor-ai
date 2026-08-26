using InfraAdvisor.Mobile.Models;
using InfraAdvisor.Mobile.Services;

namespace InfraAdvisor.Mobile.Tests;

public sealed class AppSessionTests
{
    [Fact]
    public void SignOutClearsCredentialsAndRotatesSessionId()
    {
        var session = new AppSession();
        session.SignIn(new LoginResponse("jwt", new User("u1", "person@example.com", false, false, null)));
        var authenticatedSessionId = session.SessionId;

        session.SignOut();

        Assert.False(session.IsAuthenticated);
        Assert.Null(session.Token);
        Assert.Null(session.User);
        Assert.NotEqual(authenticatedSessionId, session.SessionId);
    }

    [Fact]
    public async Task AsyncSignOutClearsCredentialsEvenWhenMediaCleanupFails()
    {
        var session = new AppSession();
        session.SignIn(new LoginResponse("jwt", new User("u1", "person@example.com", false, false, null)));
        session.RegisterSessionCleanup(() => Task.FromException(new IOException("cache cleanup failed")));

        await Assert.ThrowsAsync<IOException>(() => session.SignOutAsync());

        Assert.False(session.IsAuthenticated);
        Assert.Null(session.Token);
        Assert.Null(session.User);
    }
}
