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

    private static void OnMarkdownChanged(BindableObject bindable, object oldValue, object newValue) => ((MarkdownLabel)bindable).Render(newValue as string ?? string.Empty);

    private void Render(string markdown)
    {
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
}
