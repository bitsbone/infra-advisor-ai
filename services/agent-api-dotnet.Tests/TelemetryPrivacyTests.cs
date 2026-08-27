using InfraAdvisor.AgentApi.Models;
using InfraAdvisor.AgentApi.Observability;
using InfraAdvisor.AgentApi.Services;
using Xunit;

namespace InfraAdvisor.AgentApi.Tests;

public sealed class TelemetryPrivacyTests
{
    private const string SentinelFilename = "PRIVATE-FILENAME-DO-NOT-TRACE.jpg";
    private const string SentinelSession = "PRIVATE-SESSION-DO-NOT-TRACE";
    private const string SentinelSasUrl = "https://example.blob.core.windows.net/chat-media/audio/generated?sig=PRIVATE-SAS-DO-NOT-TRACE";

    [Fact]
    public void BlobObjectNameExcludesOriginalFilenameAndSessionId()
    {
        var name = MediaService.CreateBlobName("image", SentinelFilename, SentinelSession);

        Assert.StartsWith("image/", name);
        Assert.DoesNotContain(SentinelFilename, name);
        Assert.DoesNotContain(SentinelSession, name);
    }

    [Fact]
    public void AttachmentTelemetryExcludesSasUrlAndUsesBoundedMetadata()
    {
        var attachment = new AttachmentDto(SentinelSasUrl, "audio", "audio/webm", 1234);

        var tags = AgentService.SafeAttachmentTelemetry(attachment);
        var serialized = string.Join("|", tags.Select(tag => $"{tag.Key}={tag.Value}"));

        Assert.DoesNotContain(SentinelSasUrl, serialized);
        Assert.DoesNotContain("PRIVATE-SAS-DO-NOT-TRACE", serialized);
        Assert.Equal("audio", tags["input.modality"]);
        Assert.Equal("audio/webm", tags["audio.mime_type"]);
        Assert.Equal(1234L, tags["audio.size_bytes"]);
    }

    [Fact]
    public void FrameworkGenAiTelemetryFailsClosedForSensitiveContent()
    {
        Assert.False(TelemetryPrivacy.EnableSensitiveData);
    }

    [Fact]
    public void FeedbackTelemetryExcludesClientSessionId()
    {
        var tags = TelemetryPrivacy.SafeFeedbackTags("123", "456", "positive", SentinelSession);
        var serialized = string.Join("|", tags.Select(tag => $"{tag.Key}={tag.Value}"));

        Assert.DoesNotContain(SentinelSession, serialized);
        Assert.DoesNotContain("session", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("positive", tags["feedback.rating"]);
    }
}
