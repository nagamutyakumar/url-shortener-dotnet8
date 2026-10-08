using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Analytics;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Analytics;

public sealed class ClickTrackerTests
{
    private const string Secret = "test-secret-0123456789-0123456789-abcdef";
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, 123, TimeSpan.Zero);

    private readonly FixedTimeProvider _timeProvider = new(Now);
    private readonly ListLogger<ClickTracker> _logger = new();

    [Fact]
    public void Track_QueuesClickWithMappingIdTimeAndVisitorHash()
    {
        var queue = new RecordingClickEventQueue();
        var hasher = CreateHasher(Secret);
        var tracker = CreateTracker(queue, hasher);

        Assert.True(tracker.Track(42, Request("198.51.100.4", "UA/1.0")));

        var message = Assert.Single(queue.Messages);
        Assert.Equal(42, message.UrlMappingId);
        Assert.Equal(Now.UtcDateTime, message.ClickedAtUtc);
        Assert.Equal(DateTimeKind.Utc, message.ClickedAtUtc.Kind);
        Assert.Equal(hasher.Compute(42, IPAddress.Parse("198.51.100.4"), "UA/1.0"), message.VisitorHash);
    }

    [Fact]
    public void Track_WithoutSecret_QueuesClickWithNullVisitorHash()
    {
        var queue = new RecordingClickEventQueue();

        Assert.True(CreateTracker(queue, CreateHasher(null)).Track(42, Request("198.51.100.4", "UA/1.0")));

        Assert.Null(Assert.Single(queue.Messages).VisitorHash);
    }

    [Fact]
    public void Track_QueueFull_ReturnsFalseWithoutThrowing()
    {
        var queue = new ClickEventQueue(Options.Create(new ClickAnalyticsOptions { QueueCapacity = 1 }));
        var tracker = CreateTracker(queue, CreateHasher(Secret));

        Assert.True(tracker.Track(1, Request("198.51.100.4", "UA")));
        Assert.False(tracker.Track(1, Request("198.51.100.4", "UA")));

        Assert.Equal(1, queue.DroppedCount);
    }

    [Fact]
    public void Track_QueueThrows_ReturnsFalseAndLogsWarning()
    {
        var queue = new ThrowingClickEventQueue();

        Assert.False(CreateTracker(queue, CreateHasher(Secret)).Track(7, Request("198.51.100.4", "UA")));

        Assert.Equal(1, queue.Calls);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    private ClickTracker CreateTracker(IClickEventQueue queue, VisitorHasher hasher) =>
        new(queue, hasher, _timeProvider, _logger);

    private static VisitorHasher CreateHasher(string? secret) =>
        new(Options.Create(new ClickAnalyticsOptions { VisitorHashSecret = secret }));

    private static HttpContext Request(string ip, string userAgent)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Request.Headers.UserAgent = userAgent;
        return context;
    }
}
