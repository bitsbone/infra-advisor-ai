using InfraAdvisor.Mobile.Services;

namespace InfraAdvisor.Mobile.Tests;

public sealed class MediaValidatorTests
{
    [Theory]
    [InlineData("image/jpeg", "image")]
    [InlineData("image/webp", "image")]
    [InlineData("audio/mpeg", "audio")]
    [InlineData("audio/wav", "audio")]
    public void AcceptsBackendSupportedMedia(string mimeType, string expectedKind)
    {
        Assert.Equal(expectedKind, MediaValidator.Validate(mimeType, 1024));
    }

    [Fact]
    public void RejectsDocumentsAndOversizedFiles()
    {
        Assert.Throws<ApiException>(() => MediaValidator.Validate("application/pdf", 1024));
        Assert.Throws<ApiException>(() => MediaValidator.Validate("image/png", MediaValidator.MaximumBytes + 1));
    }
}
