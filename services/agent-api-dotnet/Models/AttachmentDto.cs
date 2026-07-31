using System.Text.Json.Serialization;

namespace InfraAdvisor.AgentApi.Models;

/// <summary>
/// A chat attachment (image or audio) already uploaded to Blob Storage via the
/// Python agent-api's POST /media/upload — this service receives the resulting
/// URL, it never handles the upload itself. See docs/agent-guides — multimodal
/// media upload architecture note (shared-endpoint decision).
/// </summary>
public record AttachmentDto(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("mime_type")] string MimeType,
    [property: JsonPropertyName("size_bytes")] long SizeBytes
);
