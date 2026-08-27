using InfraAdvisor.AgentApi.Models;
using InfraAdvisor.AgentApi.Services;
using Xunit;

namespace InfraAdvisor.AgentApi.Tests;

public sealed class RequestSecurityTests : IDisposable
{
    private readonly string? _priorConnectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");
    private readonly string? _priorContainer = Environment.GetEnvironmentVariable("AZURE_STORAGE_MEDIA_CONTAINER");
    private readonly string? _priorEndpoint = Environment.GetEnvironmentVariable("AZURE_STORAGE_BLOB_ENDPOINT");

    public RequestSecurityTests()
    {
        Environment.SetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING", "AccountName=fieldmedia;EndpointSuffix=core.windows.net");
        Environment.SetEnvironmentVariable("AZURE_STORAGE_MEDIA_CONTAINER", "chat-media");
        Environment.SetEnvironmentVariable("AZURE_STORAGE_BLOB_ENDPOINT", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING", _priorConnectionString);
        Environment.SetEnvironmentVariable("AZURE_STORAGE_MEDIA_CONTAINER", _priorContainer);
        Environment.SetEnvironmentVariable("AZURE_STORAGE_BLOB_ENDPOINT", _priorEndpoint);
    }

    private static AttachmentDto Attachment(
        string host = "fieldmedia.blob.core.windows.net",
        string container = "chat-media",
        string pathKind = "image",
        string kind = "image",
        string mimeType = "image/jpeg",
        long sizeBytes = 1024)
    {
        const string blobId = "550e8400e29b41d4a716446655440000";
        return new AttachmentDto(
            $"https://{host}/{container}/{pathKind}/{blobId}?sv=2025-01-05&se=2026-09-01T00%3A00%3A00Z&sp=r&sr=b&sig=test-signature",
            kind, mimeType, sizeBytes);
    }

    [Fact]
    public void TenantSessionKeyIsStableOpaqueAndUserScoped()
    {
        var first = TenantSessionKey.Create("user-a", "shared-session");
        Assert.Equal(first, TenantSessionKey.Create("user-a", "shared-session"));
        Assert.NotEqual(first, TenantSessionKey.Create("user-b", "shared-session"));
        Assert.DoesNotContain("user-a", first);
        Assert.DoesNotContain("shared-session", first);
    }

    [Fact]
    public void ValidServiceIssuedAttachmentIsAccepted() =>
        Assert.Equal("image/jpeg", AttachmentReferenceValidator.Validate(Attachment()).MimeType);

    [Theory]
    [InlineData("evil.example", "chat-media", "image", "image", "image/jpeg", 1024)]
    [InlineData("fieldmedia.blob.core.windows.net", "other-container", "image", "image", "image/jpeg", 1024)]
    [InlineData("fieldmedia.blob.core.windows.net", "chat-media", "audio", "image", "image/jpeg", 1024)]
    [InlineData("fieldmedia.blob.core.windows.net", "chat-media", "image", "image", "audio/webm", 1024)]
    [InlineData("fieldmedia.blob.core.windows.net", "chat-media", "image", "image", "image/jpeg", 0)]
    [InlineData("fieldmedia.blob.core.windows.net", "chat-media", "image", "image", "image/jpeg", 10485761)]
    public void RejectsForeignContainerAndMetadataMismatches(string host, string container, string pathKind, string kind, string mime, long size) =>
        Assert.Throws<InvalidAttachmentReferenceException>(() => AttachmentReferenceValidator.Validate(Attachment(host, container, pathKind, kind, mime, size)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.7")]
    [InlineData("169.254.169.254")]
    [InlineData("[::1]")]
    public void RejectsLoopbackPrivateAndLinkLocalHosts(string host)
    {
        Environment.SetEnvironmentVariable("AZURE_STORAGE_BLOB_ENDPOINT", $"https://{host}");
        Assert.Throws<InvalidAttachmentReferenceException>(() => AttachmentReferenceValidator.Validate(Attachment(host)));
    }

    [Fact]
    public void RejectsWritableOrIncompleteSas()
    {
        Assert.Throws<InvalidAttachmentReferenceException>(() => AttachmentReferenceValidator.Validate(
            Attachment() with { Url = Attachment().Url.Replace("sp=r", "sp=rw", StringComparison.Ordinal) }));
        Assert.Throws<InvalidAttachmentReferenceException>(() => AttachmentReferenceValidator.Validate(
            Attachment() with { Url = Attachment().Url.Replace("&sig=test-signature", "", StringComparison.Ordinal) }));
    }

    [Fact]
    public void PublicErrorNeverIncludesInternalExceptionMessage()
    {
        const string sentinel = "INTERNAL-DETAIL-MUST-NOT-LEAK";
        var error = PublicError.Unexpected(new InvalidOperationException(sentinel));
        Assert.Equal("The service encountered an unexpected error.", error.Detail);
        Assert.Equal("InvalidOperationException", error.ErrorType);
        Assert.DoesNotContain(sentinel, error.Detail);
    }
}
