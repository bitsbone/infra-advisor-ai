using System.Runtime.CompilerServices;

namespace InfraAdvisor.Mobile.Tests;

/// <summary>Static guards keep the example on Prism navigation instead of silently drifting back to MAUI Shell.</summary>
public sealed class PrismArchitectureGuardTests
{
    [Fact]
    public void MauiHostUsesPinnedPrismAndNoShellFilesRemain()
    {
        var appRoot = Path.Combine(MauiRoot(), "src", "InfraAdvisor.Mobile");
        var project = File.ReadAllText(Path.Combine(appRoot, "InfraAdvisor.Mobile.csproj"));
        var program = File.ReadAllText(Path.Combine(appRoot, "MauiProgram.cs"));

        Assert.Contains("Prism.DryIoc.Maui\" Version=\"9.0.537", project, StringComparison.Ordinal);
        Assert.Contains(".UsePrism(PrismStartup.Configure)", program, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(appRoot, "AppShell.xaml")));
        Assert.False(File.Exists(Path.Combine(appRoot, "AppShell.xaml.cs")));
    }

    [Fact]
    public void PrismStartupRegistersTheFourProductTabs()
    {
        var startup = File.ReadAllText(Path.Combine(MauiRoot(), "src", "InfraAdvisor.Mobile", "PrismStartup.cs"));

        Assert.Contains("RegisterForNavigation<ChatPage, ChatViewModel>", startup, StringComparison.Ordinal);
        Assert.Contains("RegisterForNavigation<HistoryPage, HistoryViewModel>", startup, StringComparison.Ordinal);
        Assert.Contains("RegisterForNavigation<ErrorLabPage, ErrorLabViewModel>", startup, StringComparison.Ordinal);
        Assert.Contains("RegisterForNavigation<InfoPage, InfoViewModel>", startup, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupRestoresOnlyTheProtectedSessionAndReestablishesUserIdentity()
    {
        var appRoot = Path.Combine(MauiRoot(), "src", "InfraAdvisor.Mobile");
        var startup = File.ReadAllText(Path.Combine(appRoot, "PrismStartup.cs"));
        var adapters = File.ReadAllText(Path.Combine(appRoot, "Services", "MauiApplicationAdapters.cs"));

        Assert.Contains("store.RestoreAsync()", startup, StringComparison.Ordinal);
        Assert.Contains("observability.IdentifyUser", startup, StringComparison.Ordinal);
        Assert.Contains("SecureStorage.Default.SetAsync", adapters, StringComparison.Ordinal);
        Assert.DoesNotContain("Preferences.Default.Set(SessionKey", adapters, StringComparison.Ordinal);
    }

    private static string MauiRoot([CallerFilePath] string sourceFile = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
