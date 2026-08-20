using System.Text.Json.Serialization;

namespace InfraAdvisor.AgentApi.Models;

/// <summary>
/// A chat attachment (image or audio) uploaded to Blob Storage — either via this
/// service's own POST /media/upload (see Services/MediaService.cs) when the .NET
/// pipeline is selected, or via the Python agent-api's POST /media/upload when
/// the Python pipeline is selected. Each backend uploads independently to the
/// same AZURE_STORAGE_MEDIA_CONTAINER container. See docs/agent-guides —
/// multimodal media upload architecture note.
/// </summary>
public record AttachmentDto(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("mime_type")] string MimeType,
    [property: JsonPropertyName("size_bytes")] long SizeBytes
);
