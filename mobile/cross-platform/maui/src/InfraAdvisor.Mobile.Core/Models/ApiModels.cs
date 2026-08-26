using System.Text.Json.Serialization;

namespace InfraAdvisor.Mobile.Models;

public enum BackendKind
{
    Python,
    DotNet,
}

public static class BackendKindExtensions
{
    public static string ApiPrefix(this BackendKind backend) => backend == BackendKind.Python ? "api" : "api-dotnet";

    public static string ApiValue(this BackendKind backend) => backend == BackendKind.Python ? "python" : "dotnet";

    public static string DisplayName(this BackendKind backend) => backend == BackendKind.Python ? "Python" : ".NET";

    public static BackendKind ParseBackend(string? value) => string.Equals(value, "dotnet", StringComparison.OrdinalIgnoreCase) ? BackendKind.DotNet : BackendKind.Python;
}

public sealed record User(
    string Id,
    string Email,
    [property: JsonPropertyName("is_admin")] bool IsAdmin,
    [property: JsonPropertyName("is_service_account")] bool IsServiceAccount,
    [property: JsonPropertyName("created_at")] string? CreatedAt);

public sealed record LoginRequest(string Email, string Password);

public sealed record LoginResponse(string Token, User User);

public sealed record ModelsResponse(IReadOnlyList<string> Models, [property: JsonPropertyName("default")] string DefaultModel);

public sealed record SuggestionsResponse(IReadOnlyList<string> Suggestions);

public sealed record ContextualSuggestionsRequest(string Query, string Answer, IReadOnlyList<string> Sources);

public sealed record MediaReference(
    string Url,
    string Kind,
    [property: JsonPropertyName("mime_type")] string MimeType,
    [property: JsonPropertyName("size_bytes")] long SizeBytes);

public sealed record MediaUploadResponse(
    string Url,
    string Kind,
    [property: JsonPropertyName("mime_type")] string MimeType,
    [property: JsonPropertyName("size_bytes")] long SizeBytes);

public sealed record QueryStreamRequest(
    string Query,
    [property: JsonPropertyName("session_id")] string SessionId,
    string Model,
    IReadOnlyList<MediaReference> Attachments);

public sealed record FeedbackRequest(
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("span_id")] string SpanId,
    string Rating,
    [property: JsonPropertyName("session_id")] string? SessionId);

public sealed record ConversationCreateRequest(string Title, string Model, string Backend);

public sealed record ConversationSummary(
    string Id,
    [property: JsonPropertyName("user_id")] string UserId,
    string Title,
    string? Model,
    string? Backend,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt,
    [property: JsonPropertyName("message_count")] int MessageCount);

public sealed record ConversationMessage(
    string Id,
    [property: JsonPropertyName("conversation_id")] string ConversationId,
    string Role,
    string Content,
    IReadOnlyList<string>? Sources,
    [property: JsonPropertyName("trace_id")] string? TraceId,
    [property: JsonPropertyName("span_id")] string? SpanId,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    IReadOnlyList<MediaReference>? Attachments);

public sealed record ConversationDetail(
    string Id,
    [property: JsonPropertyName("user_id")] string UserId,
    string Title,
    string? Model,
    string? Backend,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt,
    [property: JsonPropertyName("message_count")] int MessageCount,
    IReadOnlyList<ConversationMessage> Messages);

public sealed record ConversationListResponse(IReadOnlyList<ConversationSummary> Conversations);

public sealed record StreamEvent(
    string Event,
    string? Step = null,
    string? Status = null,
    string? Detail = null,
    string? Id = null,
    string? Name = null,
    [property: JsonPropertyName("args_json")] string? ArgsJson = null,
    [property: JsonPropertyName("result_summary")] string? ResultSummary = null,
    IReadOnlyList<string>? Sources = null,
    [property: JsonPropertyName("duration_ms")] double? DurationMs = null,
    string? Chunk = null,
    [property: JsonPropertyName("trace_id")] string? TraceId = null,
    [property: JsonPropertyName("span_id")] string? SpanId = null,
    [property: JsonPropertyName("session_id")] string? SessionId = null,
    string? Model = null,
    [property: JsonPropertyName("tools_called")] IReadOnlyList<string>? ToolsCalled = null,
    [property: JsonPropertyName("query_domain")] string? QueryDomain = null,
    [property: JsonPropertyName("message_id")] string? MessageId = null,
    string? Message = null,
    string? Category = null);
