using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Persistence.Entities;
using UrlShortener.Api.Shortening;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Shortening;

// Uses the EF Core InMemory provider for the lookup/reuse logic only. It does not enforce
// the unique short-code index or SQL Server collation; collisions are simulated with an
// interceptor and a fake conflict detector. See docs/integration-test-plan.md for what
// still needs a real SQL Server.
public sealed class UrlShorteningServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FixedTimeProvider _timeProvider = new(Now);
    private readonly FailingSaveChangesInterceptor _interceptor = new();
    private readonly UrlShortenerDbContext _dbContext;

    public UrlShorteningServiceTests()
    {
        _dbContext = CreateContext(_interceptor);
    }

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public async Task ShortenAsync_CreatesMapping_WithCanonicalUrlHashAndTimestamps()
    {
        var service = CreateService(new SequenceShortCodeGenerator("Abc1234"));

        var outcome = await service.ShortenAsync(new ShortenUrlRequest("HTTPS://Example.com:443/Path?q=1", null), default);

        var created = Assert.IsType<ShortenOutcome.Created>(outcome);
        Assert.Equal("Abc1234", created.Mapping.ShortCode);
        Assert.Equal("https://example.com/Path?q=1", created.Mapping.OriginalUrl);
        Assert.Null(created.Mapping.ExpiresAtUtc);

        var stored = Assert.Single(await ReadAllAsync());
        Assert.Equal("https://example.com/Path?q=1", stored.OriginalUrl);
        Assert.Equal(UrlCanonicalizer.ComputeHash("https://example.com/Path?q=1"), stored.OriginalUrlHash);
        Assert.True(stored.IsActive);
        Assert.Equal(Now.UtcDateTime, stored.CreatedAtUtc);
        Assert.Equal(Now.UtcDateTime, stored.UpdatedAtUtc);
    }

    [Fact]
    public async Task ShortenAsync_InvalidUrl_ReturnsErrorsAndPersistsNothing()
    {
        var generator = new SequenceShortCodeGenerator("Abc1234");
        var service = CreateService(generator);

        var outcome = await service.ShortenAsync(new ShortenUrlRequest("ftp://example.com", null), default);

        var invalid = Assert.IsType<ShortenOutcome.Invalid>(outcome);
        Assert.Contains(nameof(ShortenUrlRequest.OriginalUrl), invalid.Errors.Keys);
        Assert.Equal(0, generator.Calls);
        Assert.Empty(await ReadAllAsync());
    }

    [Fact]
    public async Task ShortenAsync_ReportsUrlAndExpirationErrorsTogether()
    {
        var service = CreateService(new SequenceShortCodeGenerator());

        var outcome = await service.ShortenAsync(
            new ShortenUrlRequest("not a url", Now.AddMinutes(-1)), default);

        var invalid = Assert.IsType<ShortenOutcome.Invalid>(outcome);
        Assert.Equal(
            new[] { nameof(ShortenUrlRequest.ExpiresAtUtc), nameof(ShortenUrlRequest.OriginalUrl) },
            invalid.Errors.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(-60_000)]
    [InlineData(0)]
    [InlineData(0.5)] // truncated to the millisecond, which equals "now"
    public async Task ShortenAsync_RejectsExpirationNotInFuture(double offsetMilliseconds)
    {
        var service = CreateService(new SequenceShortCodeGenerator("Abc1234"));
        var expires = Now.AddTicks((long)(offsetMilliseconds * TimeSpan.TicksPerMillisecond));

        var outcome = await service.ShortenAsync(new ShortenUrlRequest("https://example.com/", expires), default);

        var invalid = Assert.IsType<ShortenOutcome.Invalid>(outcome);
        Assert.Contains(nameof(ShortenUrlRequest.ExpiresAtUtc), invalid.Errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(330)] // +05:30
    [InlineData(-480)] // -08:00
    public async Task ShortenAsync_StoresExpirationAsUtcTruncatedToMilliseconds(int offsetMinutes)
    {
        var service = CreateService(new SequenceShortCodeGenerator("Abc1234"));
        var instant = Now.AddDays(1).AddTicks(12_345); // 1.2345 ms
        var expires = instant.ToOffset(TimeSpan.FromMinutes(offsetMinutes));

        var outcome = await service.ShortenAsync(new ShortenUrlRequest("https://example.com/", expires), default);

        var created = Assert.IsType<ShortenOutcome.Created>(outcome);
        var expected = Now.UtcDateTime.AddDays(1).AddMilliseconds(1);
        Assert.Equal(expected, created.Mapping.ExpiresAtUtc);
        Assert.Equal(DateTimeKind.Utc, created.Mapping.ExpiresAtUtc!.Value.Kind);
        Assert.Equal(expected, Assert.Single(await ReadAllAsync()).ExpiresAtUtc);
    }

    [Fact]
    public async Task ShortenAsync_ReusesMapping_ForSameInstantWithDifferentOffsetAndSubmilliseconds()
    {
        var service = CreateService(new SequenceShortCodeGenerator("First01", "Second2"));
        var instant = Now.AddDays(1).AddMilliseconds(123);
        await service.ShortenAsync(
            new ShortenUrlRequest("https://example.com/a", instant.AddTicks(9_000).ToOffset(TimeSpan.FromHours(2))),
            default);

        var outcome = await service.ShortenAsync(
            new ShortenUrlRequest("https://example.com/a", instant.ToOffset(TimeSpan.FromHours(-5))),
            default);

        var reused = Assert.IsType<ShortenOutcome.Reused>(outcome);
        Assert.Equal("First01", reused.Mapping.ShortCode);
        Assert.Equal(instant.UtcDateTime, reused.Mapping.ExpiresAtUtc);
    }

    [Fact]
    public async Task ShortenAsync_ReusesMapping_ForEquivalentUrlWithoutExpiration()
    {
        var service = CreateService(new SequenceShortCodeGenerator("First01", "Second2"));
        await service.ShortenAsync(new ShortenUrlRequest("https://example.com/a", null), default);

        var outcome = await service.ShortenAsync(new ShortenUrlRequest("HTTPS://EXAMPLE.COM:443/a", null), default);

        var reused = Assert.IsType<ShortenOutcome.Reused>(outcome);
        Assert.Equal("First01", reused.Mapping.ShortCode);
        Assert.Single(await ReadAllAsync());
    }

    [Fact]
    public async Task ShortenAsync_ReusesMapping_WhenExpirationMatchesExactly()
    {
        var expires = Now.AddDays(7);
        var service = CreateService(new SequenceShortCodeGenerator("First01", "Second2"));
        await service.ShortenAsync(new ShortenUrlRequest("https://example.com/a", expires), default);

        var outcome = await service.ShortenAsync(new ShortenUrlRequest("https://example.com/a", expires), default);

        var reused = Assert.IsType<ShortenOutcome.Reused>(outcome);
        Assert.Equal("First01", reused.Mapping.ShortCode);
        Assert.Equal(expires.UtcDateTime, reused.Mapping.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(null, 7)]
    [InlineData(7, null)]
    [InlineData(7, 8)]
    public async Task ShortenAsync_CreatesNewMapping_AndLeavesExisting_WhenExpirationDiffers(
        int? existingDays,
        int? requestedDays)
    {
        DateTimeOffset? existingExpiry = existingDays is { } e ? Now.AddDays(e) : null;
        DateTimeOffset? requestedExpiry = requestedDays is { } r ? Now.AddDays(r) : null;
        var service = CreateService(new SequenceShortCodeGenerator("First01", "Second2"));
        await service.ShortenAsync(new ShortenUrlRequest("https://example.com/a", existingExpiry), default);

        var outcome = await service.ShortenAsync(new ShortenUrlRequest("https://example.com/a", requestedExpiry), default);

        var created = Assert.IsType<ShortenOutcome.Created>(outcome);
        Assert.Equal("Second2", created.Mapping.ShortCode);
        var original = Assert.Single(await ReadAllAsync(), m => m.ShortCode == "First01");
        Assert.Equal(existingExpiry?.UtcDateTime, original.ExpiresAtUtc);
    }

    [Fact]
    public async Task ShortenAsync_DoesNotReuse_PathThatDiffersOnlyByCase()
    {
        var service = CreateService(new SequenceShortCodeGenerator("First01", "Second2"));
        await service.ShortenAsync(new ShortenUrlRequest("https://example.com/Docs", null), default);

        var outcome = await service.ShortenAsync(new ShortenUrlRequest("https://example.com/docs", null), default);

        Assert.IsType<ShortenOutcome.Created>(outcome);
    }

    [Fact]
    public async Task ShortenAsync_DoesNotReuse_InactiveMapping()
    {
        await SeedAsync(new UrlMapping
        {
            ShortCode = "Inactiv",
            OriginalUrl = "https://example.com/a",
            OriginalUrlHash = UrlCanonicalizer.ComputeHash("https://example.com/a"),
            IsActive = false,
        });
        var service = CreateService(new SequenceShortCodeGenerator("Second2"));

        var outcome = await service.ShortenAsync(new ShortenUrlRequest("https://example.com/a", null), default);

        var created = Assert.IsType<ShortenOutcome.Created>(outcome);
        Assert.Equal("Second2", created.Mapping.ShortCode);
    }

    [Fact]
    public async Task ShortenAsync_DoesNotReuse_RowWithSameHashButDifferentUrl()
    {
        // Simulates a SHA-256 collision: the hash matches but the URL does not.
        await SeedAsync(new UrlMapping
        {
            ShortCode = "Collide",
            OriginalUrl = "https://other.example/",
            OriginalUrlHash = UrlCanonicalizer.ComputeHash("https://example.com/a"),
        });
        var service = CreateService(new SequenceShortCodeGenerator("Second2"));

        var outcome = await service.ShortenAsync(new ShortenUrlRequest("https://example.com/a", null), default);

        Assert.IsType<ShortenOutcome.Created>(outcome);
    }

    [Fact]
    public async Task ShortenAsync_FindsExactMatch_BehindManySameHashRows()
    {
        // More same-hash, non-matching rows than the former 10-candidate cap, inserted
        // first so they sort ahead of the real match.
        var hash = UrlCanonicalizer.ComputeHash("https://example.com/a");
        for (var i = 0; i < 25; i++)
        {
            await SeedAsync(new UrlMapping
            {
                ShortCode = $"Coll{i:000}",
                OriginalUrl = $"https://other.example/{i}",
                OriginalUrlHash = hash,
            });
        }

        await SeedAsync(new UrlMapping
        {
            ShortCode = "Match01",
            OriginalUrl = "https://example.com/a",
            OriginalUrlHash = hash,
        });
        var generator = new SequenceShortCodeGenerator("Unused1");
        var service = CreateService(generator);

        var outcome = await service.ShortenAsync(new ShortenUrlRequest("https://example.com/a", null), default);

        var reused = Assert.IsType<ShortenOutcome.Reused>(outcome);
        Assert.Equal("Match01", reused.Mapping.ShortCode);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task ShortenAsync_RetriesWithNewCode_AfterShortCodeConflict()
    {
        _interceptor.Enqueue(new FakeShortCodeConflictException());
        _interceptor.Enqueue(new FakeShortCodeConflictException());
        var generator = new SequenceShortCodeGenerator("Taken01", "Taken02", "Free003");
        var service = CreateService(generator);

        var outcome = await service.ShortenAsync(new ShortenUrlRequest("https://example.com/a", null), default);

        var created = Assert.IsType<ShortenOutcome.Created>(outcome);
        Assert.Equal("Free003", created.Mapping.ShortCode);
        Assert.Equal(3, generator.Calls);
        Assert.Equal("Free003", Assert.Single(await ReadAllAsync()).ShortCode);
    }

    [Fact]
    public async Task ShortenAsync_ReturnsShortCodeUnavailable_AfterMaxAttempts()
    {
        _interceptor.FailForever = true;
        var generator = new SequenceShortCodeGenerator(
            Enumerable.Range(0, 10).Select(i => $"Code{i:000}").ToArray());
        var service = CreateService(generator);

        var outcome = await service.ShortenAsync(new ShortenUrlRequest("https://example.com/a", null), default);

        Assert.IsType<ShortenOutcome.ShortCodeUnavailable>(outcome);
        Assert.Equal(UrlShorteningService.MaxShortCodeAttempts, generator.Calls);
        Assert.Empty(_dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ShortenAsync_DoesNotRetry_OtherDbUpdateExceptions()
    {
        var failure = new DbUpdateException("Some other database failure.");
        _interceptor.Enqueue(failure);
        var generator = new SequenceShortCodeGenerator("Abc1234", "Abc5678");
        var service = CreateService(generator);

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(
            () => service.ShortenAsync(new ShortenUrlRequest("https://example.com/a", null), default));

        Assert.Same(failure, thrown);
        Assert.Equal(1, generator.Calls);
        Assert.Empty(_dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ShortenAsync_PropagatesCancellation()
    {
        var service = CreateService(new SequenceShortCodeGenerator("Abc1234"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ShortenAsync(new ShortenUrlRequest("https://example.com/a", null), cts.Token));
    }

    private UrlShorteningService CreateService(SequenceShortCodeGenerator generator) =>
        new(
            _dbContext,
            generator,
            new FakeShortCodeConflictDetector(),
            _timeProvider,
            NullLogger<UrlShorteningService>.Instance);

    private UrlShortenerDbContext CreateContext(FailingSaveChangesInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<UrlShortenerDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .AddInterceptors(new InMemoryRowVersionInterceptor());
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new UrlShortenerDbContext(builder.Options, _timeProvider);
    }

    private async Task SeedAsync(UrlMapping mapping)
    {
        await using var context = CreateContext();
        context.UrlMappings.Add(mapping);
        await context.SaveChangesAsync();
    }

    private async Task<List<UrlMapping>> ReadAllAsync()
    {
        await using var context = CreateContext();
        return await context.UrlMappings.AsNoTracking().ToListAsync();
    }
}
