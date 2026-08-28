using System.Text.RegularExpressions;

namespace InfraAdvisor.Mobile.Controls;

/// <summary>
/// Renders the small Markdown subset returned by the advisor without evaluating HTML. HTTP(S) links become explicit tappable spans and all other content remains plain text.
/// </summary>
public sealed partial class MarkdownLabel : Label
{
    public static readonly BindableProperty MarkdownProperty = BindableProperty.Create(nameof(Markdown), typeof(string), typeof(MarkdownLabel), string.Empty, propertyChanged: OnMarkdownChanged);

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownLabel() => LineHeight = 1.25;

    private static void OnMarkdownChanged(BindableObject bindable, object oldValue, object newValue) => ((MarkdownLabel)bindable).Render(newValue as string ?? string.Empty);

    private void Render(string markdown)
    {
        markdown = NormalizeForDisplay(markdown);
        var formatted = new FormattedString();
        var index = 0;
        foreach (Match match in LinkPattern().Matches(markdown))
        {
            AddText(formatted, markdown[index..match.Index]);
            var target = match.Groups[2].Value;
            var link = new Span { Text = match.Groups[1].Value, TextColor = Color.FromArgb("#1D4ED8"), TextDecorations = TextDecorations.Underline };
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                {
                    await Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred);
                }
            };
            link.GestureRecognizers.Add(tap);
            formatted.Spans.Add(link);
            index = match.Index + match.Length;
        }

        AddText(formatted, markdown[index..]);
        FormattedText = formatted;
    }

    /// <summary>
    /// Normalizes the lightweight Markdown emitted by API agents into readable mobile paragraphs and lists. The method intentionally does not interpret HTML or arbitrary Markdown extensions.
    /// </summary>
    public static string NormalizeForDisplay(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        normalized = NumberedListBoundaryPattern().Replace(normalized, "\n$1");
        normalized = BulletListBoundaryPattern().Replace(normalized, "\n$1");
        normalized = HeadingPattern().Replace(normalized, "**$1**");
        normalized = BulletPrefixPattern().Replace(normalized, "• ");

        var output = new List<string>();
        var previousWasStructured = false;
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var isStructured = StructuredLinePattern().IsMatch(line);
            if (isStructured && output.Count > 0 && output[^1].Length > 0 && !previousWasStructured)
            {
                output.Add(string.Empty);
            }

            if (line.Length == 0 && output.Count > 0 && output[^1].Length == 0)
            {
                continue;
            }

            output.Add(line);
            previousWasStructured = isStructured;
        }

        return string.Join('\n', output).Trim();
    }

    private static void AddText(FormattedString formatted, string value)
    {
        var index = 0;
        foreach (Match match in BoldPattern().Matches(value))
        {
            if (match.Index > index) formatted.Spans.Add(new Span { Text = value[index..match.Index] });
            formatted.Spans.Add(new Span { Text = match.Groups[1].Value, FontAttributes = FontAttributes.Bold });
            index = match.Index + match.Length;
        }

        if (index < value.Length) formatted.Spans.Add(new Span { Text = value[index..] });
    }

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^\s)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldPattern();

    [GeneratedRegex(@"(?<!^)(?<!\n)\s+(\d+\.\s+)")]
    private static partial Regex NumberedListBoundaryPattern();

    [GeneratedRegex(@"(?<!^)(?<!\n)\s+([-*]\s+)")]
    private static partial Regex BulletListBoundaryPattern();

    [GeneratedRegex(@"^#{1,6}\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^\s*[-*]\s+", RegexOptions.Multiline)]
    private static partial Regex BulletPrefixPattern();

    [GeneratedRegex(@"^\s*(?:•|\d+\.|\*\*)")]
    private static partial Regex StructuredLinePattern();
}
