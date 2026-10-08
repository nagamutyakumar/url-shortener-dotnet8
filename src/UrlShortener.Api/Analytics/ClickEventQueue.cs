using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace UrlShortener.Api.Analytics;

/// <summary>A click captured at redirect time. Holds no raw IP address or user agent.</summary>
public sealed record ClickEventMessage(long UrlMappingId, DateTime ClickedAtUtc, byte[]? VisitorHash);

public interface IClickEventQueue
{
    /// <summary>Never blocks. Returns false when the event was dropped.</summary>
    bool TryEnqueue(ClickEventMessage message);
}

/// <summary>
/// Bounded in-process buffer between redirects and <see cref="ClickEventWriter"/>.
/// </summary>
/// <remarks>
/// Queue-full policy: drop the new event and count it. Redirects only call TryWrite, which
/// with <see cref="BoundedChannelFullMode.Wait"/> returns false instead of waiting when the
/// channel is full. (The Drop* modes would make TryWrite report success while silently
/// discarding an event, so drops could not be counted.) Buffered events live only in
/// memory and are lost if the process crashes.
/// </remarks>
public sealed class ClickEventQueue : IClickEventQueue
{
    private readonly Channel<ClickEventMessage> _channel;
    private long _droppedCount;

    public ClickEventQueue(IOptions<ClickAnalyticsOptions> options)
    {
        _channel = Channel.CreateBounded<ClickEventMessage>(
            new BoundedChannelOptions(options.Value.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    public ChannelReader<ClickEventMessage> Reader => _channel.Reader;

    /// <summary>Events rejected because the queue was full or completed, since startup.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public bool TryEnqueue(ClickEventMessage message)
    {
        if (_channel.Writer.TryWrite(message))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    /// <summary>Stops accepting events; the reader finishes once the buffer is drained.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
