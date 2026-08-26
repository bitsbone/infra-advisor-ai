namespace InfraAdvisor.Mobile.Services;

public static class MediaValidator
{
    public const long MaximumBytes = 10 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> SupportedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = "image",
        ["image/png"] = "image",
        ["image/webp"] = "image",
        ["audio/webm"] = "audio",
        ["audio/wav"] = "audio",
        ["audio/x-wav"] = "audio",
        ["audio/mpeg"] = "audio",
        ["audio/ogg"] = "audio",
    };

    public static string Validate(string mimeType, long sizeBytes)
    {
        if (!SupportedTypes.TryGetValue(mimeType, out var kind))
        {
            throw new ApiException("Choose a JPEG, PNG, WebP, WAV, MP3, WebM, or OGG file.", category: "unsupported_media");
        }

        if (sizeBytes <= 0 || sizeBytes > MaximumBytes)
        {
            throw new ApiException("Attachments must be between 1 byte and 10 MB.", category: "media_size");
        }

        return kind;
    }
}
