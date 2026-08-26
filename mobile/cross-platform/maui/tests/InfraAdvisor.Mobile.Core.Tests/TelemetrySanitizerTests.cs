using InfraAdvisor.Mobile.Services;

namespace InfraAdvisor.Mobile.Tests;

public sealed class TelemetrySanitizerTests
{
    [Fact]
    public void ResourceUrlsDropQueryStringsAndFragments()
    {
        Assert.Equal("https://storage.example.test/container/blob", TelemetrySanitizer.SanitizeUrl("https://storage.example.test/container/blob?sv=secret#fragment"));
        Assert.Equal("/relative/path", TelemetrySanitizer.SanitizeUrl("/relative/path?token=secret"));
    }

    [Fact]
    public void SensitiveApplicationAttributesAreRejected()
    {
        var filtered = TelemetrySanitizer.FilterAttributes(new Dictionary<string, object>
        {
            ["backend"] = "python",
            ["prompt"] = "private question",
            ["authorization.token"] = "secret",
            ["upload_url"] = "https://storage.example.test/blob?sig=secret",
            ["size_bytes"] = 10,
        });

        Assert.Equal("python", filtered["backend"]);
        Assert.Equal(10, filtered["size_bytes"]);
        Assert.DoesNotContain("prompt", filtered.Keys);
        Assert.DoesNotContain("authorization.token", filtered.Keys);
        Assert.DoesNotContain("upload_url", filtered.Keys);
    }

    [Fact]
    public void PromptLikeAutomaticActionNamesAreRedacted()
    {
        Assert.Equal("redacted input action", TelemetrySanitizer.SanitizeActionName("What current federal procurement opportunities exist in Texas?"));
        Assert.Equal("SendPrompt", TelemetrySanitizer.SanitizeActionName("SendPrompt"));
    }

    [Fact]
    public void DiagnosticTextStripsUrlQueriesWithoutRemovingStackFrames()
    {
        var sanitized = TelemetrySanitizer.SanitizeDiagnosticText("GET https://example.test/api?token=secret failed\nat Example.Run()");

        Assert.DoesNotContain("secret", sanitized);
        Assert.Contains("https://example.test/api", sanitized);
        Assert.Contains("Example.Run", sanitized);
    }
}
