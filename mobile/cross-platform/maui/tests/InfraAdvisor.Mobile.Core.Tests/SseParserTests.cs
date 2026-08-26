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

    [Fact]
    public async Task ParsesEventsAcrossSingleByteNetworkFragments()
    {
        const string payload = "event: text_chunk\ndata: {\"chunk\":\"fragmented\"}\n\n";
        await using var stream = new FragmentedReadStream(Encoding.UTF8.GetBytes(payload));

        var events = new List<Models.StreamEvent>();
        await foreach (var item in SseParser.ParseAsync(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        Assert.Equal("fragmented", Assert.Single(events).Chunk);
    }

    [Fact]
    public async Task CancellationInterruptsAnIncompleteStream()
    {
        await using var stream = new NeverEndingStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in SseParser.ParseAsync(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellation.Token))
            {
            }
        });
    }

    private sealed class FragmentedReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
