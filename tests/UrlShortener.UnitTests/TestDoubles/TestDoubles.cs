using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    public bool FailForever { get; set; }

    public void Enqueue(Exception exception) => _failures.Enqueue(exception);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
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
/// enforces the required column, so this fills it in for inserted mappings.
/// </summary>
public sealed class InMemoryRowVersionInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        foreach (var entry in eventData.Context!.ChangeTracker.Entries<UrlShortener.Api.Persistence.Entities.UrlMapping>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.RowVersion = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
            }
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
