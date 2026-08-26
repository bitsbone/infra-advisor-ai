using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using InfraAdvisor.Mobile.Models;

namespace InfraAdvisor.Mobile.Services;

/// <summary>
/// Parses Server-Sent Events one line at a time, so arbitrary network-buffer boundaries do not affect event delivery.
/// </summary>
public static class SseParser
{
    public static async IAsyncEnumerable<StreamEvent> ParseAsync(
        Stream stream,
        JsonSerializerOptions jsonOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var data = new StringBuilder();
        string? eventName = null;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return Deserialize(eventName, data.ToString(), jsonOptions);
                    data.Clear();
                    eventName = null;
                }

                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line[5..].TrimStart());
            }
        }

        if (data.Length > 0)
        {
            yield return Deserialize(eventName, data.ToString(), jsonOptions);
        }
    }

    private static StreamEvent Deserialize(string? eventName, string json, JsonSerializerOptions options)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<StreamEvent>(json, options) ?? throw new JsonException("The event body was empty.");
            return string.IsNullOrWhiteSpace(parsed.Event) && !string.IsNullOrWhiteSpace(eventName) ? parsed with { Event = eventName } : parsed;
        }
        catch (JsonException exception)
        {
            throw new ApiException("The server returned an invalid streaming event.", category: "malformed_stream", innerException: exception);
        }
    }
}
