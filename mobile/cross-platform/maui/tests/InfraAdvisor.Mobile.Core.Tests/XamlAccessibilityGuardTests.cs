using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace InfraAdvisor.Mobile.Tests;

/// <summary>
/// Lightweight guards keep accessibility contracts visible without loading platform handlers in the unit-test process.
/// Device tests remain responsible for validating VoiceOver, TalkBack, focus order, and actual rendered geometry.
/// </summary>
public sealed class XamlAccessibilityGuardTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2009/xaml";

    [Fact]
    public void EveryPageUsesSafeAreasAndHasAStableAutomationId()
    {
        foreach (var (path, document) in PageDocuments())
        {
            Assert.Equal("Container", document.Root?.Attribute("SafeAreaEdges")?.Value);
            Assert.False(string.IsNullOrWhiteSpace(document.Root?.Attribute("AutomationId")?.Value), $"{path} must expose a page AutomationId.");
        }
    }

    [Fact]
    public void SharedHeadingAndTextScalingContractsAreEnabled()
    {
        var styles = XDocument.Load(Path.Combine(MauiRoot(), "src", "InfraAdvisor.Mobile", "Resources", "Styles", "Styles.xaml"));

        Assert.Equal("Level1", Style(styles, "PageTitle").Elements().Single(element => element.Attribute("Property")?.Value == "SemanticProperties.HeadingLevel").Attribute("Value")?.Value);
        Assert.Equal("Level2", Style(styles, "SectionTitle").Elements().Single(element => element.Attribute("Property")?.Value == "SemanticProperties.HeadingLevel").Attribute("Value")?.Value);
        Assert.DoesNotContain(AllMauiXaml(), item => item.document.Descendants().Attributes("FontAutoScalingEnabled").Any(attribute => attribute.Value.Equals("False", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void InputsHaveSemanticNamesAndAutomationIds()
    {
        var inputNames = new HashSet<string>(StringComparer.Ordinal) { "Entry", "Editor", "Picker", "BackendSegmentedControl" };

        foreach (var (path, document) in PageDocuments())
        {
            foreach (var input in document.Descendants().Where(element => inputNames.Contains(element.Name.LocalName)))
            {
                Assert.False(string.IsNullOrWhiteSpace(input.Attribute("AutomationId")?.Value), $"{path}: {input.Name.LocalName} needs an AutomationId.");
                Assert.False(string.IsNullOrWhiteSpace(input.Attribute("SemanticProperties.Description")?.Value), $"{path}: {input.Name.LocalName} needs a semantic description independent of its placeholder.");
            }
        }
    }

    [Fact]
    public void ButtonsNeverOverrideTheSharedTouchTargetBelow48()
    {
        foreach (var (path, document) in AllMauiXaml())
        {
            foreach (var element in document.Descendants().Where(element => element.Name.LocalName is "Button" or "Style"))
            {
                var directMinimum = element.Attribute("MinimumHeightRequest")?.Value;
                if (double.TryParse(directMinimum, out var height))
                {
                    Assert.True(height >= 48, $"{path}: {element.Name.LocalName} overrides the touch target with {height}.");
                }

                foreach (var setter in element.Elements().Where(child => child.Attribute("Property")?.Value == "MinimumHeightRequest"))
                {
                    Assert.True(double.TryParse(setter.Attribute("Value")?.Value, out height) && height >= 48, $"{path}: a style defines a touch target below 48.");
                }
            }
        }
    }

    [Fact]
    public void ChatRetainsSmallHeightScrollingAndWrappingGuards()
    {
        var chat = XDocument.Load(Path.Combine(MauiRoot(), "src", "InfraAdvisor.Mobile", "Views", "ChatPage.xaml"));

        Assert.Contains(chat.Descendants().Where(element => element.Name.LocalName == "ScrollView"), element => element.Attribute("AutomationId")?.Value == "AdvisorConfigurationScroll");
        Assert.Contains(chat.Descendants().Where(element => element.Name.LocalName == "ScrollView"), element => element.Attribute("AutomationId")?.Value == "EmptyAdvisorScroll");
        Assert.DoesNotContain(chat.Descendants().Where(element => element.Name.LocalName == "HorizontalStackLayout" && element.Attribute("BindableLayout.ItemsSource") is not null), element => element.Ancestors().All(ancestor => ancestor.Name.LocalName != "ScrollView"));
        Assert.Contains(chat.Descendants().Where(element => element.Name.LocalName == "FlexLayout"), element => element.Attribute("Wrap")?.Value == "Wrap");
    }

    [Fact]
    public void DynamicWorkflowPagesUseScopedScreenReaderAnnouncements()
    {
        foreach (var page in new[] { "LoginPage.xaml.cs", "ChatPage.xaml.cs", "HistoryPage.xaml.cs", "ErrorLabPage.xaml.cs" })
        {
            var source = File.ReadAllText(Path.Combine(MauiRoot(), "src", "InfraAdvisor.Mobile", "Views", page));
            Assert.Contains("SemanticScreenReader.Default.Announce", source);
            Assert.Contains("PropertyChanged +=", source);
            Assert.Contains("PropertyChanged -=", source);
        }
    }

    [Fact]
    public void ChatSheetsHideTheUnderlyingAdvisorAndExposeStableCloseTargets()
    {
        var chat = XDocument.Load(Path.Combine(MauiRoot(), "src", "InfraAdvisor.Mobile", "Views", "ChatPage.xaml"));
        var protectedLayers = chat.Descendants().Where(element => element.Attribute("AutomationProperties.ExcludedWithChildren")?.Value == "{Binding IsModalVisible}").ToArray();

        Assert.Equal(2, protectedLayers.Length);
        Assert.All(protectedLayers, element => Assert.Equal("{Binding IsModalVisible}", element.Attribute("InputTransparent")?.Value));
        Assert.Contains(chat.Descendants(), element => element.Attribute(Xaml + "Name")?.Value == "CloseConversationHistoryButton");
        Assert.Contains(chat.Descendants(), element => element.Attribute(Xaml + "Name")?.Value == "CloseEvidenceButton");
    }

    private static XElement Style(XDocument document, string key) => document.Descendants().Single(element => element.Name.LocalName == "Style" && element.Attribute(Xaml + "Key")?.Value == key);

    private static IEnumerable<(string path, XDocument document)> PageDocuments() => Directory.EnumerateFiles(Path.Combine(MauiRoot(), "src", "InfraAdvisor.Mobile", "Views"), "*.xaml").Order().Select(path => (path, XDocument.Load(path)));

    private static IEnumerable<(string path, XDocument document)> AllMauiXaml() => Directory.EnumerateFiles(Path.Combine(MauiRoot(), "src", "InfraAdvisor.Mobile"), "*.xaml", SearchOption.AllDirectories).Order().Select(path => (path, XDocument.Load(path)));

    private static string MauiRoot([CallerFilePath] string sourceFile = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
