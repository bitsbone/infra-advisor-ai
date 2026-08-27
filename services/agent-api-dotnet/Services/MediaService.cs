using System.Diagnostics;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using InfraAdvisor.AgentApi.Models;

namespace InfraAdvisor.AgentApi.Services;

// Chat attachment storage for multimodal input — mirrors services/agent-api/src/media.py
// so both backends can point at the same AZURE_STORAGE_MEDIA_CONTAINER container using
// identical env var names, blob-naming convention, and SAS semantics. Uploaded
// images/audio land in Azure Blob Storage; callers get back a read-only SAS URL rather
// than raw bytes, so attachments flow through the request body / conversation history /
// vision content without bloating any of them.
public class MediaService
{
    private static readonly Dictionary<string, string> AllowedContentTypes = new()
    {
        ["image/jpeg"] = "image",
        ["image/png"] = "image",
        ["image/webp"] = "image",
        ["audio/webm"] = "audio",
        ["audio/wav"] = "audio",
        ["audio/mpeg"] = "audio",
        ["audio/ogg"] = "audio",
    };

    private const long MaxUploadBytes = 10 * 1024 * 1024; // 10 MB

    private static readonly ActivitySource ActivitySource =
        new(Observability.TelemetrySetup.ActivitySourceName);

    private readonly ILogger<MediaService> _logger;

    public MediaService(ILogger<MediaService> logger)
    {
        _logger = logger;
    }

    private static string MediaContainerName() =>
        Environment.GetEnvironmentVariable("AZURE_STORAGE_MEDIA_CONTAINER") ?? "chat-media";

    private static int SasExpiryHours() =>
        int.TryParse(Environment.GetEnvironmentVariable("MEDIA_SAS_EXPIRY_HOURS"), out var hours)
            ? hours
            : 168;

    public async Task<AttachmentDto> UploadAsync(
        Stream fileStream,
        long contentLength,
        string filename,
        string contentType,
        string sessionId,
        CancellationToken ct)
    {
        // Browsers (MediaRecorder in particular) often send params after the
        // type, e.g. "audio/webm;codecs=opus" — match on the bare mime type.
        var bareContentType = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (!AllowedContentTypes.TryGetValue(bareContentType, out var kind))
            throw new UnsupportedMediaTypeException(contentType);
        if (contentLength > MaxUploadBytes)
            throw new MediaTooLargeException(contentLength);

        var containerName = MediaContainerName();
        // Never persist user-controlled filenames or chat session IDs in an
        // object key. Content-Type metadata is sufficient to serve the blob.
        var blobName = CreateBlobName(kind, filename, sessionId);

        using var activity = ActivitySource.StartActivity("azure.blob.upload");
        activity?.SetTag("blob.container", containerName);
        activity?.SetTag("media.kind", kind);
        activity?.SetTag("blob.size_bytes", contentLength);
        activity?.SetTag("blob.content_type", bareContentType);

        var connectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "AZURE_STORAGE_CONNECTION_STRING env var is required for media upload.");

        var serviceClient = new BlobServiceClient(connectionString);
        var containerClient = serviceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        await blobClient.UploadAsync(
            fileStream,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = bareContentType },
            },
            ct);

        var expiry = DateTimeOffset.UtcNow.AddHours(SasExpiryHours());
        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b",
            ExpiresOn = expiry,
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        var sasUri = blobClient.GenerateSasUri(sasBuilder);

        return new AttachmentDto(
            Url: sasUri.ToString(),
            Kind: kind,
            MimeType: bareContentType,
            SizeBytes: contentLength);
    }

    internal static string CreateBlobName(string kind, string filename, string sessionId)
    {
        // Keep the ignored values explicit at this privacy boundary so future
        // refactors cannot accidentally reintroduce either into object names.
        _ = filename;
        _ = sessionId;
        return $"{kind}/{Guid.NewGuid():N}";
    }
}

public class UnsupportedMediaTypeException(string contentType)
    : Exception($"Unsupported content type: {contentType}");

public class MediaTooLargeException(long sizeBytes)
    : Exception($"File too large: {sizeBytes} bytes");
