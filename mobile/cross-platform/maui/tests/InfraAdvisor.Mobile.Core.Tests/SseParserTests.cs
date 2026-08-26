using System.Text;
using System.Text.Json;
using InfraAdvisor.Mobile.Services;

namespace InfraAdvisor.Mobile.Tests;

public sealed class SseParserTests
{
    [Fact]
    public async Task ParsesNamedAndMultilineEvents()
    {
        const string payload = "event: text_chunk\ndata: {\"chunk\":\ndata: \"hello\"}\n\nevent: done\ndata: {\"trace_id\":\"42\"}\n\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var events = new List<Models.StreamEvent>();

        await foreach (var item in SseParser.ParseAsync(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        Assert.Equal(2, events.Count);
        Assert.Equal("text_chunk", events[0].Event);
        Assert.Equal("hello", events[0].Chunk);
        Assert.Equal("42", events[1].TraceId);
    }

    [Fact]
    public async Task RejectsMalformedEventWithoutExposingItsBody()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: not-json\n\n"));

        var exception = await Assert.ThrowsAsync<ApiException>(async () =>
        {
            await foreach (var _ in SseParser.ParseAsync(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Equal("malformed_stream", exception.Category);
        Assert.DoesNotContain("not-json", exception.Message);
    }
}
