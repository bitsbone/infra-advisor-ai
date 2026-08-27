using System.Net;
using InfraAdvisor.AgentApi.Models;

namespace InfraAdvisor.AgentApi.Services;

/// <summary>
/// Fail-closed validation for client-supplied attachment references. The
/// accepted shape exactly matches MediaService: one configured public HTTPS
/// Blob host/container, an opaque kind/GUID object path, and a blob-scoped,
/// read-only SAS. No network request occurs until this boundary succeeds.
/// </summary>
public static class AttachmentReferenceValidator
{
    private const long MaxUploadBytes = 10 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> AllowedContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = "image",
            ["image/png"] = "image",
            ["image/webp"] = "image",
            ["audio/webm"] = "audio",
            ["audio/wav"] = "audio",
            ["audio/mpeg"] = "audio",
            ["audio/ogg"] = "audio",
        };

    public static AttachmentDto Validate(AttachmentDto attachment)
    {
        var mimeType = (attachment.MimeType ?? "").Split(';', 2)[0].Trim().ToLowerInvariant();
        if (!AllowedContentTypes.TryGetValue(mimeType, out var expectedKind) ||
            !string.Equals(expectedKind, attachment.Kind, StringComparison.Ordinal))
            throw new InvalidAttachmentReferenceException();
        if (attachment.SizeBytes <= 0 || attachment.SizeBytes > MaxUploadBytes)
            throw new InvalidAttachmentReferenceException();
        if (!Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            (!uri.IsDefaultPort && uri.Port != 443))
            throw new InvalidAttachmentReferenceException();

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (!IsPublicHost(host) || !string.Equals(host, ConfiguredBlobHost(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidAttachmentReferenceException();

        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        var container = Environment.GetEnvironmentVariable("AZURE_STORAGE_MEDIA_CONTAINER") ?? "chat-media";
        if (path.Length != 3 || path[0] != container || path[1] != attachment.Kind ||
            !Guid.TryParseExact(path[2], "N", out _))
            throw new InvalidAttachmentReferenceException();

        var query = ParseQuery(uri.Query);
        var required = new[] { "sig", "se", "sp", "sr", "sv" };
        if (required.Any(key => !query.TryGetValue(key, out var values) || values.Count != 1) ||
            query["sp"][0] != "r" || query["sr"][0] != "b" || string.IsNullOrEmpty(query["sig"][0]))
            throw new InvalidAttachmentReferenceException();

        return attachment with { MimeType = mimeType };
    }

    private static string ConfiguredBlobHost()
    {
        var values = ParseConnectionString(Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING") ?? "");
        var endpoint = Environment.GetEnvironmentVariable("AZURE_STORAGE_BLOB_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint)) values.TryGetValue("BlobEndpoint", out endpoint);
        if (!string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            return endpointUri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (!values.TryGetValue("AccountName", out var account) || string.IsNullOrWhiteSpace(account))
            throw new InvalidAttachmentReferenceException();
        values.TryGetValue("EndpointSuffix", out var suffix);
        suffix = string.IsNullOrWhiteSpace(suffix) ? "core.windows.net" : suffix;
        return $"{account}.blob.{suffix}".TrimEnd('.').ToLowerInvariant();
    }

    private static Dictionary<string, string> ParseConnectionString(string connectionString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            if (index > 0) result[part[..index].Trim()] = part[(index + 1)..].Trim();
        }
        return result;
    }

    private static Dictionary<string, List<string>> ParseQuery(string queryString)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var part in queryString.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0].Replace('+', ' '));
            var value = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1].Replace('+', ' ')) : "";
            if (!result.TryGetValue(key, out var values)) result[key] = values = [];
            values.Add(value);
        }
        return result;
    }

    private static bool IsPublicHost(string host)
    {
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IPAddress.TryParse(host, out var address)) return true;
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return !(bytes[0] == 10 || bytes[0] == 127 || bytes[0] == 0 ||
                     (bytes[0] == 169 && bytes[1] == 254) ||
                     (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                     (bytes[0] == 192 && bytes[1] == 168));
        return !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.IPv6None);
    }
}

public sealed class InvalidAttachmentReferenceException : Exception;
