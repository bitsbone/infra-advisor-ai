using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InfraAdvisor.Mobile.Models;

namespace InfraAdvisor.Mobile.Services;

/// <summary>
/// The sole HTTP boundary for the MAUI application. Payloads and credentials are never logged; Datadog observes this shared HttpClient automatically.
/// </summary>
public sealed class InfraAdvisorApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly AppSession session;
    private readonly IRumSessionProvider rumSessionProvider;

    public InfraAdvisorApiClient(HttpClient httpClient, AppSession session, IRumSessionProvider rumSessionProvider)
    {
        this.httpClient = httpClient;
        this.session = session;
        this.rumSessionProvider = rumSessionProvider;
    }

    public async Task<LoginResponse> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("auth/login", new LoginRequest(email, password), JsonOptions, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<LoginResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<ModelsResponse> GetModelsAsync(CancellationToken cancellationToken = default) => GetAsync<ModelsResponse>(ApiPath("models"), cancellationToken);

    public async Task<IReadOnlyList<SuggestionItem>> GetInitialSuggestionsAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<SuggestionsResponse>(ApiPath("suggestions/initial"), cancellationToken).ConfigureAwait(false);
        return response.Suggestions;
    }

    public async Task<IReadOnlyList<SuggestionItem>> GetContextualSuggestionsAsync(string query, string answer, IReadOnlyList<string> sources, CancellationToken cancellationToken = default)
    {
        var response = await SendJsonAsync<SuggestionsResponse>(HttpMethod.Post, ApiPath("suggestions"), new ContextualSuggestionsRequest(query, answer, sources), cancellationToken).ConfigureAwait(false);
        return response.Suggestions;
    }

    public async Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, ApiPath("conversations"), content: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("conversations");
        return array.Deserialize<IReadOnlyList<ConversationSummary>>(JsonOptions) ?? [];
    }

    public Task<ConversationDetail> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default) => GetAsync<ConversationDetail>(ApiPath($"conversations/{Uri.EscapeDataString(conversationId)}"), cancellationToken);

    public Task<ConversationDetail> CreateConversationAsync(string title, string model, CancellationToken cancellationToken = default) =>
        SendJsonAsync<ConversationDetail>(HttpMethod.Post, ApiPath("conversations"), new ConversationCreateRequest(title, model, session.Backend.ApiValue()), cancellationToken);

    public async Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, ApiPath($"conversations/{Uri.EscapeDataString(conversationId)}"), content: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendFeedbackAsync(FeedbackRequest feedback, CancellationToken cancellationToken = default)
    {
        using var response = await SendJsonRequestAsync(HttpMethod.Post, ApiPath("feedback"), feedback, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task TriggerDemoApiErrorAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, ApiPath("observability-demo/not-found"), content: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MediaUploadResponse> UploadMediaAsync(Stream stream, string fileName, string mimeType, long sizeBytes, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        MediaValidator.Validate(mimeType, sizeBytes);
        using var multipart = new MultipartFormDataContent();
        using var file = new ProgressStreamContent(stream, sizeBytes, progress, cancellationToken);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mimeType);
        multipart.Add(file, "file", Path.GetFileName(fileName));
        using var response = await SendAsync(HttpMethod.Post, ApiPath("media/upload"), multipart, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<MediaUploadResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<StreamEvent> StreamQueryAsync(QueryStreamRequest query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, ApiPath("query/stream"));
        request.Content = JsonContent.Create(query, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var streamEvent in SseParser.ParseAsync(stream, JsonOptions, cancellationToken).ConfigureAwait(false))
        {
            yield return streamEvent;
        }
    }

    private string ApiPath(string relative) => $"{session.Backend.ApiPrefix()}/{relative}";

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, content: null, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendJsonRequestAsync(method, path, body, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> SendJsonRequestAsync(HttpMethod method, string path, object body, CancellationToken cancellationToken) =>
        SendAsync(method, path, JsonContent.Create(body, options: JsonOptions), cancellationToken);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path);
        request.Content = content;
        return await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (session.Token is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Headers.TryAddWithoutValidation("X-Session-ID", session.SessionId);
        if (session.User is { } user)
        {
            request.Headers.TryAddWithoutValidation("X-User-ID", user.Id);
        }
        if (session.ConversationId is { } conversationId)
        {
            request.Headers.TryAddWithoutValidation("X-Conversation-ID", conversationId);
        }

        if (rumSessionProvider.CurrentSessionId is { } rumSessionId)
        {
            request.Headers.TryAddWithoutValidation("X-DD-RUM-Session-ID", rumSessionId);
        }

        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw new JsonException("The response body was empty.");
        }
        catch (JsonException exception)
        {
            throw new ApiException("The server returned a response this app could not read.", (int)response.StatusCode, "malformed_response", exception);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your session has expired. Sign in again.",
            HttpStatusCode.TooManyRequests => "The service is busy. Wait a moment and try again.",
            >= HttpStatusCode.InternalServerError => "The service encountered an error. Try again shortly.",
            _ => "The request could not be completed.",
        };

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (document.RootElement.TryGetProperty("detail", out var detail) || document.RootElement.TryGetProperty("message", out detail))
            {
                var serverMessage = detail.GetString();
                if (!string.IsNullOrWhiteSpace(serverMessage) && serverMessage.Length <= 240)
                {
                    message = serverMessage;
                }
            }
        }
        catch (JsonException)
        {
            // A sanitized status-based message is safer than returning an arbitrary HTML or proxy response body.
        }

        throw new ApiException(message, (int)response.StatusCode, "http_error");
    }

    private sealed class ProgressStreamContent(Stream source, long length, IProgress<double>? progress, CancellationToken cancellationToken) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var buffer = new byte[81920];
            long sent = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                sent += read;
                progress?.Report(length == 0 ? 0 : (double)sent / length);
            }
        }

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = length;
            return true;
        }
    }
}
