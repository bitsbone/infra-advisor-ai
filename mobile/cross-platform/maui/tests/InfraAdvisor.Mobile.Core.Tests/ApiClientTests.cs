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

        var progress = new InlineProgress();
        var uploaded = await client.UploadMediaAsync(stream, "sample.png", "image/png", 4, progress, TestContext.Current.CancellationToken);

        Assert.Equal("image", uploaded.Kind);
        Assert.Equal("/api/media/upload", handler.Path);
        Assert.Contains("name=file", handler.Body);
        Assert.Contains("filename=sample.png", handler.Body);
        Assert.Equal(1, progress.Value);
    }

    [Fact]
    public async Task SuggestionsDecodeBackendLabelAndQueryContract()
    {
        var handler = new RecordingHandler("{\"suggestions\":[{\"label\":\"Federal procurement\",\"query\":\"Find current opportunities\"}]}");
        var client = CreateClient(handler, SignedInSession());

        var suggestions = await client.GetInitialSuggestionsAsync(TestContext.Current.CancellationToken);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("Federal procurement", suggestion.Label);
        Assert.Equal("Find current opportunities", suggestion.Query);
    }

    [Fact]
    public async Task ConversationDetailRestoresStepsAndAttachments()
    {
        const string body = """
            {"id":"c1","user_id":"u1","title":"Inspection","model":"gpt-4.1-mini","backend":"python","message_count":1,"messages":[{"id":"m1","conversation_id":"c1","role":"assistant","content":"Done","sources":["https://example.test/source"],"trace_id":"42","span_id":"7","created_at":"2026-08-25T12:00:00Z","steps":[{"kind":"tool","id":"tool-1","name":"get_bridge_condition","status":"ok","args_json":null,"result_summary":"3 bridges","sources":["FHWA"],"duration_ms":12.5,"detail":null}],"attachments":[{"url":"https://storage.example.test/item","kind":"image","mime_type":"image/png","size_bytes":4}]}]}
            """;
        var client = CreateClient(new RecordingHandler(body), SignedInSession());

        var conversation = await client.GetConversationAsync("c1", TestContext.Current.CancellationToken);

        var message = Assert.Single(conversation.Messages);
        Assert.Equal("get_bridge_condition", Assert.Single(message.Steps!).Name);
        Assert.Equal("image", Assert.Single(message.Attachments!).Kind);
    }

    [Fact]
    public async Task StreamQueryUsesConversationRumAndSerializedAttachmentContract()
    {
        var session = SignedInSession();
        session.ConversationId = "conversation-1";
        var handler = new RecordingHandler("event: done\ndata: {\"trace_id\":\"42\",\"span_id\":\"7\"}\n\n");
        var client = CreateClient(handler, session, new StaticRumSessionProvider("rum-1"));
        var request = new QueryStreamRequest("Inspect this", session.SessionId, "gpt-4.1-mini", [new MediaReference("https://storage.example.test/item", "image", "image/png", 4)]);

        var events = new List<StreamEvent>();
        await foreach (var streamEvent in client.StreamQueryAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(streamEvent);
        }

        Assert.Equal("/api/query/stream", handler.Path);
        Assert.Equal("conversation-1", handler.ConversationId);
        Assert.Equal("rum-1", handler.RumSessionId);
        Assert.Contains("\"session_id\"", handler.Body);
        Assert.Contains("\"attachments\"", handler.Body);
        Assert.Equal("42", Assert.Single(events).TraceId);
    }

    [Fact]
    public async Task FeedbackUsesBackendSnakeCaseContract()
    {
        var handler = new RecordingHandler("{}", HttpStatusCode.NoContent);
        var client = CreateClient(handler, SignedInSession());

        await client.SendFeedbackAsync(new FeedbackRequest("42", "7", "positive", "session-1"), TestContext.Current.CancellationToken);

        Assert.Equal("/api/feedback", handler.Path);
        Assert.Contains("\"trace_id\":\"42\"", handler.Body);
        Assert.Contains("\"span_id\":\"7\"", handler.Body);
        Assert.Contains("\"session_id\":\"session-1\"", handler.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "session has expired")]
    [InlineData(HttpStatusCode.TooManyRequests, "service is busy")]
    public async Task CommonHttpFailuresReturnReadableMessages(HttpStatusCode status, string expected)
    {
        var client = CreateClient(new RecordingHandler("{}", status), SignedInSession());

        var exception = await Assert.ThrowsAsync<ApiException>(() => client.GetModelsAsync(TestContext.Current.CancellationToken));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedSuccessResponseReturnsSanitizedDecodeError()
    {
        var client = CreateClient(new RecordingHandler("not-json"), SignedInSession());

        var exception = await Assert.ThrowsAsync<ApiException>(() => client.GetModelsAsync(TestContext.Current.CancellationToken));

        Assert.Equal("malformed_response", exception.Category);
        Assert.DoesNotContain("not-json", exception.Message);
    }

    [Fact]
    public async Task RequestCancellationPropagatesWithoutRetry()
    {
        var client = CreateClient(new BlockingHandler(), SignedInSession());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetModelsAsync(cancellation.Token));
    }

    private static InfraAdvisorApiClient CreateClient(HttpMessageHandler handler, AppSession session, IRumSessionProvider? rumSessionProvider = null) => new(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, session, rumSessionProvider ?? new EmptyRumSessionProvider());

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
        public string? ConversationId { get; private set; }
        public string? RumSessionId { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            SessionId = request.Headers.TryGetValues("X-Session-ID", out var values) ? values.Single() : null;
            UserId = request.Headers.TryGetValues("X-User-ID", out var userValues) ? userValues.Single() : null;
            ConversationId = request.Headers.TryGetValues("X-Conversation-ID", out var conversationValues) ? conversationValues.Single() : null;
            RumSessionId = request.Headers.TryGetValues("X-DD-RUM-Session-ID", out var rumValues) ? rumValues.Single() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StaticRumSessionProvider(string sessionId) : IRumSessionProvider
    {
        public string? CurrentSessionId => sessionId;
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class InlineProgress : IProgress<double>
    {
        public double Value { get; private set; }
        public void Report(double value) => Value = value;
    }
}
