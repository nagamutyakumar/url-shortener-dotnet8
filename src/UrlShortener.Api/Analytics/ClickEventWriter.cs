using Microsoft.Extensions.Options;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Persistence.Entities;

namespace UrlShortener.Api.Analytics;

/// <summary>
/// Drains <see cref="ClickEventQueue"/> and inserts click events in batches.
/// </summary>
/// <remarks>
/// Delivery is best-effort, not guaranteed. Events are lost when the queue is full (counted
/// and logged), when a batch insert fails (logged; no retry, so a database outage cannot
/// build an unbounded backlog), when shutdown cannot flush the buffer within
/// <see cref="ShutdownFlushTimeout"/> (logged), and when the process crashes (not observable).
/// Every exception is caught per batch: an unhandled one would stop the host.
/// </remarks>
public sealed class ClickEventWriter : BackgroundService
{
    public static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly ClickEventQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VisitorHasher _visitorHasher;
    private readonly ILogger<ClickEventWriter> _logger;
    private readonly int _batchSize;
    private long _reportedDroppedCount;

    public ClickEventWriter(
        ClickEventQueue queue,
        IServiceScopeFactory scopeFactory,
        VisitorHasher visitorHasher,
        IOptions<ClickAnalyticsOptions> options,
        ILogger<ClickEventWriter> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _visitorHasher = visitorHasher;
        _logger = logger;
        _batchSize = options.Value.BatchSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_visitorHasher.IsEnabled)
        {
            _logger.LogWarning(
                "{Setting} is not configured: click events are stored without a visitor hash and " +
                "unique visitors will be reported as unavailable.",
                $"{ClickAnalyticsOptions.SectionName}:{nameof(ClickAnalyticsOptions.VisitorHashSecret)}");
        }

        var batch = new List<ClickEventMessage>(_batchSize);
        try
        {
            // Returns false once the queue is completed and empty.
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                FillBatch(batch);
                await PersistAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping; fall through to the bounded flush below.
        }

        await FlushOnShutdownAsync(batch);
    }

    private async Task FlushOnShutdownAsync(List<ClickEventMessage> batch)
    {
        // Any click arriving from now on is dropped and counted.
        _queue.Complete();

        using var timeout = new CancellationTokenSource(ShutdownFlushTimeout);
        while (!timeout.IsCancellationRequested && FillBatch(batch) > 0)
        {
            await PersistAsync(batch, timeout.Token);
        }

        var unwritten = 0;
        while (_queue.Reader.TryRead(out _))
        {
            unwritten++;
        }

        if (unwritten > 0)
        {
            _logger.LogWarning(
                "Shutdown flush timed out; {Count} buffered click events were not persisted.", unwritten);
        }

        ReportDroppedEvents();
    }

    private int FillBatch(List<ClickEventMessage> batch)
    {
        while (batch.Count < _batchSize && _queue.Reader.TryRead(out var message))
        {
            batch.Add(message);
        }

        return batch.Count;
    }

    private async Task PersistAsync(List<ClickEventMessage> batch, CancellationToken cancellationToken)
    {
        try
        {
            // The writer is a singleton, so each batch gets its own scope and DbContext rather
            // than sharing a long-lived, non-thread-safe context.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<UrlShortenerDbContext>();
            dbContext.ClickEvents.AddRange(batch.Select(m => new ClickEvent
            {
                UrlMappingId = m.UrlMappingId,
                ClickedAtUtc = m.ClickedAtUtc,
                VisitorHash = m.VisitorHash,
            }));
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Shutdown interrupted a click event insert; {Count} events were not persisted.", batch.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist {Count} click events; they were discarded.", batch.Count);
        }
        finally
        {
            batch.Clear();
        }

        ReportDroppedEvents();
    }

    private void ReportDroppedEvents()
    {
        var dropped = _queue.DroppedCount;
        if (dropped > _reportedDroppedCount)
        {
            _logger.LogWarning(
                "Click event queue was full or shutting down; dropped {Dropped} events ({Total} since startup).",
                dropped - _reportedDroppedCount,
                dropped);
            _reportedDroppedCount = dropped;
        }
    }
}
