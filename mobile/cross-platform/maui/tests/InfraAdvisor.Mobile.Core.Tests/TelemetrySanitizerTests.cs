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

    [Theory]
    [InlineData("at Example.Run() in /Users/private.user/build/Example.cs:line 42")]
    [InlineData(@"at Example.Run() in C:\agent\_work\private.user\Example.cs:line 42")]
    public void DiagnosticTextRemovesAbsoluteBuildPaths(string stackFrame)
    {
        var sanitized = TelemetrySanitizer.SanitizeDiagnosticText(stackFrame);

        Assert.DoesNotContain("private.user", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/Users/", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\agent", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Example.Run", sanitized);
        Assert.Contains("<source>:line 42", sanitized);
    }
}
