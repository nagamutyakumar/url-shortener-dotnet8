using Microsoft.Extensions.Options;
using UrlShortener.Api.Analytics;

namespace UrlShortener.UnitTests.Analytics;

public sealed class ClickEventQueueTests
{
    private static readonly DateTime ClickedAt = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TryEnqueue_WithinCapacity_QueuesInOrder()
    {
        var queue = CreateQueue(capacity: 2);

        Assert.True(queue.TryEnqueue(Message(1)));
        Assert.True(queue.TryEnqueue(Message(2)));

        Assert.Equal(0, queue.DroppedCount);
        Assert.True(queue.Reader.TryRead(out var first));
        Assert.True(queue.Reader.TryRead(out var second));
        Assert.Equal(new long[] { 1, 2 }, new[] { first!.UrlMappingId, second!.UrlMappingId });
    }

    [Fact]
    public void TryEnqueue_WhenFull_DropsNewEventAndCountsIt()
    {
        var queue = CreateQueue(capacity: 2);
        queue.TryEnqueue(Message(1));
        queue.TryEnqueue(Message(2));

        Assert.False(queue.TryEnqueue(Message(3)));
        Assert.False(queue.TryEnqueue(Message(4)));

        Assert.Equal(2, queue.DroppedCount);
        // The buffered events are kept; the new ones are the ones dropped.
        Assert.Equal(2, queue.Reader.Count);
        Assert.True(queue.Reader.TryRead(out var first));
        Assert.Equal(1, first!.UrlMappingId);
    }

    [Fact]
    public void TryEnqueue_AfterReaderFreesSpace_AcceptsAgain()
    {
        var queue = CreateQueue(capacity: 1);
        queue.TryEnqueue(Message(1));
        Assert.False(queue.TryEnqueue(Message(2)));

        Assert.True(queue.Reader.TryRead(out _));

        Assert.True(queue.TryEnqueue(Message(3)));
        Assert.Equal(1, queue.DroppedCount);
    }

    [Fact]
    public async Task TryEnqueue_DoesNotWaitForSpace()
    {
        var queue = CreateQueue(capacity: 1);
        queue.TryEnqueue(Message(1));

        // Would hang on a blocking write; TryEnqueue must return synchronously.
        var enqueue = Task.Run(() => queue.TryEnqueue(Message(2)));

        Assert.Same(enqueue, await Task.WhenAny(enqueue, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.False(await enqueue);
    }

    [Fact]
    public void TryEnqueue_AfterComplete_DropsAndCounts()
    {
        var queue = CreateQueue(capacity: 10);
        queue.Complete();

        Assert.False(queue.TryEnqueue(Message(1)));
        Assert.Equal(1, queue.DroppedCount);
    }

    [Theory]
    [InlineData(0, 100, null, false)]
    [InlineData(1_000_001, 100, null, false)]
    [InlineData(10, 0, null, false)]
    [InlineData(10, 1_001, null, false)]
    [InlineData(10, 100, "too-short", false)]
    [InlineData(1, 1, null, true)]
    [InlineData(10, 100, "", true)]
    [InlineData(1_000_000, 1_000, "0123456789abcdef0123456789abcdef", true)]
    public void Options_Validation(int capacity, int batchSize, string? secret, bool valid)
    {
        var options = new ClickAnalyticsOptions
        {
            QueueCapacity = capacity,
            BatchSize = batchSize,
            VisitorHashSecret = secret,
        };

        Assert.Equal(valid, ClickAnalyticsOptions.IsValid(options));
    }

    private static ClickEventQueue CreateQueue(int capacity) =>
        new(Options.Create(new ClickAnalyticsOptions { QueueCapacity = capacity }));

    private static ClickEventMessage Message(long urlMappingId) => new(urlMappingId, ClickedAt, null);
}
