using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Caching;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.ShortCodes;

namespace UrlShortener.UnitTests.TestDoubles;

public sealed class FixedTimeProvider : TimeProvider
{
    public FixedTimeProvider(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

public sealed class SequenceShortCodeGenerator : IShortCodeGenerator
{
    private readonly Queue<string> _codes;

    public SequenceShortCodeGenerator(params string[] codes) => _codes = new Queue<string>(codes);

    public int Calls { get; private set; }

    public string Generate()
    {
        Calls++;
        return _codes.Count > 0 ? _codes.Dequeue() : throw new InvalidOperationException("No codes left.");
    }
}

/// <summary>Stands in for a SQL Server unique-index violation on the short code.</summary>
public sealed class FakeShortCodeConflictException : DbUpdateException
{
    public FakeShortCodeConflictException()
        : base("Simulated short-code unique index violation.")
    {
    }
}

public sealed class FakeShortCodeConflictDetector : IShortCodeConflictDetector
{
    public bool IsShortCodeConflict(DbUpdateException exception) => exception is FakeShortCodeConflictException;
}

/// <summary>
/// Throws the queued exceptions from successive SaveChanges calls, then lets saves through.
/// The InMemory provider does not enforce unique indexes, so this is how collisions are simulated.
/// </summary>
public sealed class FailingSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly Queue<Exception> _failures = new();
    private int _attempts;

    public bool FailForever { get; set; }

    /// <summary>SaveChanges calls seen so far, failed or not.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    public void Enqueue(Exception exception) => _failures.Enqueue(exception);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _attempts);

        if (FailForever)
        {
            throw new FakeShortCodeConflictException();
        }

        if (_failures.TryDequeue(out var failure))
        {
            throw failure;
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>
/// SQL Server generates rowversion values; the InMemory provider does not but still
/// enforces the required column, so this fills it in for inserted mappings and replaces
/// it for updated ones. The InMemory provider compares the original value with the stored
/// one on update, so stale writes fail with DbUpdateConcurrencyException as on SQL Server.
/// </summary>
public sealed class InMemoryRowVersionInterceptor : SaveChangesInterceptor
{
    private static long _nextVersion;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        foreach (var entry in eventData.Context!.ChangeTracker.Entries<UrlShortener.Api.Persistence.Entities.UrlMapping>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.RowVersion = BitConverter.GetBytes(Interlocked.Increment(ref _nextVersion));
            }
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>
/// Runs a callback once, just before the next SaveChanges is executed. Used to make another
/// writer change a row between a service's read and its write.
/// </summary>
public sealed class BeforeSaveChangesInterceptor : SaveChangesInterceptor
{
    private Func<Task>? _callback;

    public void RunOnce(Func<Task> callback) => _callback = callback;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _callback, null) is { } callback)
        {
            await callback();
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

public sealed class RecordingClickEventQueue : IClickEventQueue
{
    public ConcurrentQueue<ClickEventMessage> Messages { get; } = new();

    public bool TryEnqueue(ClickEventMessage message)
    {
        Messages.Enqueue(message);
        return true;
    }
}

public sealed class ThrowingClickEventQueue : IClickEventQueue
{
    public int Calls;

    public bool TryEnqueue(ClickEventMessage message)
    {
        Interlocked.Increment(ref Calls);
        throw new InvalidOperationException("Simulated analytics queue failure.");
    }
}

/// <summary>Captures log entries so tests can assert that failures and drops are reported.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception), exception));
}

/// <summary>
/// In-memory stand-in for Redis. Records every call (with TTLs) and can fail any operation, so
/// the cache-aside and invalidation policies can be tested without a Redis server.
/// </summary>
public sealed class FakeRedirectCache : IRedirectCache
{
    public ConcurrentDictionary<string, CachedRedirect> Entries { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Gets { get; } = new();

    public ConcurrentQueue<(string ShortCode, CachedRedirect Entry, TimeSpan TimeToLive)> Sets { get; } = new();

    public ConcurrentQueue<string> Removes { get; } = new();

    public bool FailGets { get; set; }

    public bool FailSets { get; set; }

    public bool FailRemoves { get; set; }

    public Task<CachedRedirect?> GetAsync(string shortCode)
    {
        Gets.Enqueue(shortCode);
        if (FailGets)
        {
            throw new InvalidOperationException("Simulated Redis lookup failure.");
        }

        return Task.FromResult(Entries.TryGetValue(shortCode, out var entry) ? entry : null);
    }

    public Task SetAsync(string shortCode, CachedRedirect entry, TimeSpan timeToLive)
    {
        Sets.Enqueue((shortCode, entry, timeToLive));
        if (FailSets)
        {
            throw new InvalidOperationException("Simulated Redis write failure.");
        }

        Entries[shortCode] = entry;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string shortCode)
    {
        Removes.Enqueue(shortCode);
        if (FailRemoves)
        {
            throw new InvalidOperationException("Simulated Redis delete failure.");
        }

        Entries.TryRemove(shortCode, out _);
        return Task.CompletedTask;
    }
}
