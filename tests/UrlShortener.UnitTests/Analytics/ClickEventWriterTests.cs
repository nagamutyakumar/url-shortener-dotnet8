using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Persistence;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Analytics;

// Drives the writer directly against InMemory. Completing the queue ends ExecuteAsync once the
// buffer is drained, so these runs are deterministic without polling.
public sealed class ClickEventWriterTests : IAsyncDisposable
{
    private const string Secret = "test-secret-0123456789-0123456789-abcdef";
    private static readonly DateTime ClickedAt = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FailingSaveChangesInterceptor _failures = new();
    private readonly GateSaveChangesInterceptor _gate = new();
    private readonly ListLogger<ClickEventWriter> _logger = new();
    private readonly ServiceProvider _services;

    public ClickEventWriterTests()
    {
        _services = new ServiceCollection()
            .AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(ClickedAt)))
            .AddDbContext<UrlShortenerDbContext>(options => options
                .UseInMemoryDatabase(_databaseName)
                .AddInterceptors(_failures, _gate))
            // Resolving the scoped DbContext from the root provider would throw.
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    [Fact]
    public async Task PersistsQueuedEventsInBatches()
    {
        var queue = CreateQueue();
        var hash = new byte[32];
        hash[0] = 0xAB;
        queue.TryEnqueue(new ClickEventMessage(1, ClickedAt, hash));
        for (var i = 2; i <= 5; i++)
        {
            queue.TryEnqueue(new ClickEventMessage(i, ClickedAt.AddSeconds(i), null));
        }

        await RunToCompletionAsync(CreateWriter(queue, batchSize: 2), queue);

        var events = await LoadEventsAsync();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, events.Select(e => e.UrlMappingId));
        Assert.Equal(hash, events[0].VisitorHash);
        Assert.Null(events[1].VisitorHash);
        Assert.Equal(ClickedAt.AddSeconds(5), events[4].ClickedAtUtc);
        Assert.All(events, e => Assert.Null(e.CountryCode));
        Assert.All(events, e => Assert.Null(e.Browser));
        Assert.Equal(3, _failures.Attempts); // 2 + 2 + 1
    }

    [Fact]
    public async Task FailedBatch_IsLoggedAndDiscarded_LaterBatchesStillPersist()
    {
        var queue = CreateQueue();
        for (var i = 1; i <= 4; i++)
        {
            queue.TryEnqueue(new ClickEventMessage(i, ClickedAt, null));
        }

        _failures.Enqueue(new DbUpdateException("Simulated SQL failure."));
        var writer = CreateWriter(queue, batchSize: 2);

        await RunToCompletionAsync(writer, queue);

        Assert.True(writer.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(new long[] { 3, 4 }, (await LoadEventsAsync()).Select(e => e.UrlMappingId));
        var error = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("2 click events", error.Message);
        Assert.IsType<DbUpdateException>(error.Exception);
    }

    [Fact]
    public async Task DatabaseDown_WriterKeepsRunningAndDoesNotFault()
    {
        var queue = CreateQueue();
        _failures.FailForever = true;
        var writer = CreateWriter(queue, batchSize: 1);
        await writer.StartAsync(CancellationToken.None);

        queue.TryEnqueue(new ClickEventMessage(1, ClickedAt, null));
        queue.TryEnqueue(new ClickEventMessage(2, ClickedAt, null));
        queue.Complete();
        await writer.ExecuteTask!.WaitAsync(Timeout);

        Assert.True(writer.ExecuteTask.IsCompletedSuccessfully);
        Assert.Empty(await LoadEventsAsync());
        Assert.Equal(2, _logger.Entries.Count(e => e.Level == LogLevel.Error));
    }

    [Fact]
    public async Task DroppedEvents_AreReportedAsWarning()
    {
        var queue = CreateQueue(capacity: 1);
        queue.TryEnqueue(new ClickEventMessage(1, ClickedAt, null));
        queue.TryEnqueue(new ClickEventMessage(2, ClickedAt, null));
        queue.TryEnqueue(new ClickEventMessage(3, ClickedAt, null));

        await RunToCompletionAsync(CreateWriter(queue), queue);

        Assert.Single(await LoadEventsAsync());
        var warning = Assert.Single(_logger.Entries, e => e.Message.Contains("dropped"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("dropped 2 events (2 since startup)", warning.Message);
    }

    [Fact]
    public async Task Shutdown_FlushesBufferedEvents_AndRejectsLateOnes()
    {
        var queue = CreateQueue();
        var writer = CreateWriter(queue, batchSize: 1);
        _gate.Arm();
        await writer.StartAsync(CancellationToken.None);

        // Event 1 is mid-insert (held by the gate) when shutdown begins; 2 and 3 are buffered.
        queue.TryEnqueue(new ClickEventMessage(1, ClickedAt, null));
        await _gate.Entered.WaitAsync(Timeout);
        queue.TryEnqueue(new ClickEventMessage(2, ClickedAt, null));
        queue.TryEnqueue(new ClickEventMessage(3, ClickedAt, null));

        await writer.StopAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(new long[] { 2, 3 }, (await LoadEventsAsync()).Select(e => e.UrlMappingId));
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Shutdown interrupted") && e.Message.Contains("1 events"));
        Assert.False(queue.TryEnqueue(new ClickEventMessage(4, ClickedAt, null)));
        Assert.Equal(1, queue.DroppedCount);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(Secret, false)]
    public async Task MissingVisitorHashSecret_IsLoggedAtStartup(string? secret, bool expectWarning)
    {
        var queue = CreateQueue();

        await RunToCompletionAsync(CreateWriter(queue, secret: secret), queue);

        Assert.Equal(expectWarning, _logger.Entries.Any(e => e.Message.Contains("VisitorHashSecret")));
    }

    private static ClickEventQueue CreateQueue(int capacity = 100) =>
        new(Options.Create(new ClickAnalyticsOptions { QueueCapacity = capacity }));

    private ClickEventWriter CreateWriter(ClickEventQueue queue, int batchSize = 100, string? secret = Secret)
    {
        var options = Options.Create(new ClickAnalyticsOptions { BatchSize = batchSize, VisitorHashSecret = secret });
        return new ClickEventWriter(
            queue,
            _services.GetRequiredService<IServiceScopeFactory>(),
            new VisitorHasher(options),
            options,
            _logger);
    }

    private static async Task RunToCompletionAsync(ClickEventWriter writer, ClickEventQueue queue)
    {
        queue.Complete();
        await writer.StartAsync(CancellationToken.None);
        await writer.ExecuteTask!.WaitAsync(Timeout);
    }

    private async Task<List<UrlShortener.Api.Persistence.Entities.ClickEvent>> LoadEventsAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UrlShortenerDbContext>()
            .ClickEvents.OrderBy(e => e.UrlMappingId).ToListAsync();
    }

    /// <summary>When armed, holds the next SaveChanges until it is cancelled.</summary>
    private sealed class GateSaveChangesInterceptor : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Task Entered => _entered.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _entered.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            }

            return result;
        }
    }
}
