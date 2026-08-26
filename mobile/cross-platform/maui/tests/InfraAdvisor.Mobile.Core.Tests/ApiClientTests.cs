using System.Net;
using System.Text;
using InfraAdvisor.Mobile.Models;
using InfraAdvisor.Mobile.Services;

namespace InfraAdvisor.Mobile.Tests;

public sealed class ApiClientTests
{
    [Fact]
    public async Task LoginSerializesCredentialsWithoutAuthorizationHeader()
    {
        var handler = new RecordingHandler("{\"token\":\"jwt\",\"user\":{\"id\":\"u1\",\"email\":\"person@example.com\",\"is_admin\":false,\"is_service_account\":false,\"created_at\":null}}");
        var client = CreateClient(handler, new AppSession());

        var response = await client.LoginAsync("person@example.com", "secret", TestContext.Current.CancellationToken);

        Assert.Equal("jwt", response.Token);
        Assert.Equal("/auth/login", handler.Path);
        Assert.Null(handler.Authorization);
        Assert.Contains("\"password\":\"secret\"", handler.Body);
    }

    [Fact]
    public async Task AuthenticatedRequestsUseBackendBearerAndSessionHeaders()
    {
        var session = SignedInSession();
        session.Backend = BackendKind.DotNet;
        var handler = new RecordingHandler("{\"models\":[\"gpt-4.1-mini\"],\"default\":\"gpt-4.1-mini\"}");
        var client = CreateClient(handler, session);

        await client.GetModelsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("/api-dotnet/models", handler.Path);
        Assert.Equal("Bearer jwt", handler.Authorization);
        Assert.Equal(session.SessionId, handler.SessionId);
        Assert.Equal("u1", handler.UserId);
    }

    [Theory]
    [InlineData("[{\"id\":\"c1\",\"user_id\":\"u1\",\"title\":\"One\",\"message_count\":0}]")]
    [InlineData("{\"conversations\":[{\"id\":\"c1\",\"user_id\":\"u1\",\"title\":\"One\",\"message_count\":0}]}")]
    public async Task ConversationListAcceptsDotNetArrayAndPythonWrapper(string body)
    {
        var client = CreateClient(new RecordingHandler(body), SignedInSession());

        var conversations = await client.GetConversationsAsync(TestContext.Current.CancellationToken);

        Assert.Single(conversations);
        Assert.Equal("c1", conversations[0].Id);
    }

    [Fact]
    public async Task HttpErrorsReturnSanitizedStatusMessageForNonJsonBody()
    {
        var handler = new RecordingHandler("<html>upstream details</html>", HttpStatusCode.InternalServerError);
        var client = CreateClient(handler, SignedInSession());

        var exception = await Assert.ThrowsAsync<ApiException>(() => client.GetModelsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(500, exception.StatusCode);
        Assert.DoesNotContain("upstream", exception.Message);
    }

    [Fact]
    public async Task MediaUploadUsesValidatedMultipartWithoutBufferingApplicationTelemetry()
    {
        var handler = new RecordingHandler("{\"url\":\"https://storage.example.test/blob\",\"kind\":\"image\",\"mime_type\":\"image/png\",\"size_bytes\":4}");
        var client = CreateClient(handler, SignedInSession());
        await using var stream = new MemoryStream([1, 2, 3, 4]);

        var uploaded = await client.UploadMediaAsync(stream, "sample.png", "image/png", 4, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("image", uploaded.Kind);
        Assert.Equal("/api/media/upload", handler.Path);
        Assert.Contains("name=file", handler.Body);
        Assert.Contains("filename=sample.png", handler.Body);
    }

    private static InfraAdvisorApiClient CreateClient(HttpMessageHandler handler, AppSession session) => new(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, session, new EmptyRumSessionProvider());

    private static AppSession SignedInSession()
    {
        var session = new AppSession();
        session.SignIn(new LoginResponse("jwt", new User("u1", "person@example.com", false, false, null)));
        return session;
    }

    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Authorization { get; private set; }
        public string? SessionId { get; private set; }
        public string? UserId { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            SessionId = request.Headers.TryGetValues("X-Session-ID", out var values) ? values.Single() : null;
            UserId = request.Headers.TryGetValues("X-User-ID", out var userValues) ? userValues.Single() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
