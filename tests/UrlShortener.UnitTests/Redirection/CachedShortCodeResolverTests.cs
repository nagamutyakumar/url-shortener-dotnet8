using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Caching;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Persistence.Entities;
using UrlShortener.Api.Redirection;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Redirection;

// Cache-aside behaviour of ShortCodeResolver with an in-memory fake cache and the InMemory
// database. Entries placed only in the fake cache (not in SQL) prove that a hit skips SQL.
public sealed class CachedShortCodeResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxTimeToLive = TimeSpan.FromMinutes(5);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FixedTimeProvider _timeProvider = new(Now);
    private readonly FakeRedirectCache _cache = new();

    [Fact]
    public async Task Miss_ResolvesFromSqlAndPopulatesCacheWithMaxTtl()
    {
        var mapping = await SeedAsync(Mapping("Abc1234"));

        var resolved = await ResolveAsync("Abc1234");

        Assert.Equal(new ResolvedShortCode(mapping.Id, "https://example.com/target"), resolved);
        var set = Assert.Single(_cache.Sets);
        Assert.Equal("Abc1234", set.ShortCode);
        Assert.Equal(new CachedRedirect(mapping.Id, "https://example.com/target", null), set.Entry);
        Assert.Equal(MaxTimeToLive, set.TimeToLive);
    }

    [Fact]
    public async Task Miss_LinkExpiringSoon_TtlIsRemainingLifetime()
    {
        var expires = Now.UtcDateTime.AddSeconds(30);
        var mapping = Mapping("Abc1234");
        mapping.ExpiresAtUtc = expires;
        await SeedAsync(mapping);

        await ResolveAsync("Abc1234");

        var set = Assert.Single(_cache.Sets);
        Assert.Equal(TimeSpan.FromSeconds(30), set.TimeToLive);
        Assert.Equal(expires, set.Entry.ExpiresAtUtc);
    }

    [Fact]
    public async Task Miss_SubMillisecondNow_TtlStillEndsExactlyAtExpiration()
    {
        var expires = Now.UtcDateTime.AddSeconds(30);
        var mapping = Mapping("Abc1234");
        mapping.ExpiresAtUtc = expires;
        await SeedAsync(mapping);
        _timeProvider.UtcNow = Now.AddTicks(9_999);

        await ResolveAsync("Abc1234");

        // "now" is truncated to the millisecond, as for the SQL comparison; the TTL can never
        // reach past ExpiresAtUtc by more than that sub-millisecond remainder.
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(_cache.Sets).TimeToLive);
    }

    [Fact]
    public async Task Hit_ReturnsCachedValueWithoutSql()
    {
        // Not in SQL at all: only the cache can produce this result.
        _cache.Entries["Cache01"] = new CachedRedirect(99, "https://example.com/cached", null);

        var resolved = await ResolveAsync("Cache01");

        Assert.Equal(new ResolvedShortCode(99, "https://example.com/cached"), resolved);
        Assert.Empty(_cache.Sets);
    }

    [Fact]
    public async Task SecondLookup_IsServedFromCache()
    {
        await SeedAsync(Mapping("Abc1234"));

        await ResolveAsync("Abc1234");
        await RemoveFromSqlAsync("Abc1234");
        var second = await ResolveAsync("Abc1234");

        Assert.Equal("https://example.com/target", second?.OriginalUrl);
        Assert.Single(_cache.Sets);
    }

    [Fact]
    public async Task Hit_PastStoredExpiration_IsIgnoredAndSqlDecides()
    {
        // Stale entry still present in Redis (e.g. clock skew), link expired in SQL too.
        var mapping = Mapping("Abc1234");
        mapping.ExpiresAtUtc = Now.UtcDateTime;
        await SeedAsync(mapping);
        _cache.Entries["Abc1234"] = new CachedRedirect(mapping.Id, "https://example.com/target", Now.UtcDateTime);

        Assert.Null(await ResolveAsync("Abc1234"));
        Assert.Empty(_cache.Sets);
    }

    [Fact]
    public async Task Hit_PastStoredExpiration_ButExtendedInSql_RedirectsAndRefreshesCache()
    {
        // The expiration was extended but the invalidation was lost; SQL is authoritative.
        var mapping = Mapping("Abc1234");
        mapping.ExpiresAtUtc = Now.UtcDateTime.AddDays(1);
        await SeedAsync(mapping);
        _cache.Entries["Abc1234"] = new CachedRedirect(mapping.Id, "https://example.com/target", Now.UtcDateTime.AddMilliseconds(-1));

        Assert.NotNull(await ResolveAsync("Abc1234"));
        Assert.Equal(Now.UtcDateTime.AddDays(1), _cache.Entries["Abc1234"].ExpiresAtUtc);
    }

    [Fact]
    public async Task Hit_ExpirationBoundary_RedirectsUntilExpiresAtThenNot()
    {
        var expires = Now.UtcDateTime.AddMinutes(1);
        _cache.Entries["Cache01"] = new CachedRedirect(99, "https://example.com/cached", expires);

        _timeProvider.UtcNow = Now.AddMinutes(1).AddTicks(-1);
        Assert.NotNull(await ResolveAsync("Cache01"));

        _timeProvider.UtcNow = Now.AddMinutes(1);
        Assert.Null(await ResolveAsync("Cache01"));
    }

    [Theory]
    [InlineData("Zzz9999")]
    [InlineData("Inact01")]
    [InlineData("Expir01")]
    [InlineData("abc1234")]
    [InlineData("ABC1234")]
    public async Task NonResolvingCodes_AreNeverCached(string code)
    {
        var inactive = Mapping("Inact01");
        inactive.IsActive = false;
        var expired = Mapping("Expir01");
        expired.ExpiresAtUtc = Now.UtcDateTime;
        await SeedAsync(Mapping("Abc1234"), inactive, expired);

        Assert.Null(await ResolveAsync(code));
        Assert.Empty(_cache.Sets);
    }

    [Fact]
    public async Task WrongCase_DoesNotUseAnotherCodesEntry()
    {
        _cache.Entries["Abc1234"] = new CachedRedirect(99, "https://example.com/cached", null);

        Assert.Null(await ResolveAsync("abc1234"));
        Assert.Equal(new[] { "abc1234" }, _cache.Gets);
    }

    [Theory]
    [InlineData("Abc123")]
    [InlineData("Abc-123")]
    [InlineData("Abc/123")]
    public async Task MalformedCode_DoesNotTouchCache(string code)
    {
        Assert.Null(await ResolveAsync(code));
        Assert.Empty(_cache.Gets);
        Assert.Empty(_cache.Sets);
    }

    [Fact]
    public async Task GetFails_FallsBackToSqlAndStillTriesToPopulate()
    {
        await SeedAsync(Mapping("Abc1234"));
        _cache.FailGets = true;
        var logger = new ListLogger<ShortCodeResolver>();

        var resolved = await ResolveAsync("Abc1234", logger);

        Assert.Equal("https://example.com/target", resolved?.OriginalUrl);
        Assert.Single(_cache.Sets);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Exception is not null);
    }

    [Fact]
    public async Task SetFails_StillResolves()
    {
        await SeedAsync(Mapping("Abc1234"));
        _cache.FailSets = true;

        Assert.Equal("https://example.com/target", (await ResolveAsync("Abc1234"))?.OriginalUrl);
    }

    [Fact]
    public async Task GetAndSetFail_ResolvesFromSqlEveryTime()
    {
        await SeedAsync(Mapping("Abc1234"));
        _cache.FailGets = true;
        _cache.FailSets = true;

        Assert.NotNull(await ResolveAsync("Abc1234"));
        Assert.NotNull(await ResolveAsync("Abc1234"));
    }

    private static UrlMapping Mapping(string shortCode) => new()
    {
        ShortCode = shortCode,
        OriginalUrl = "https://example.com/target",
        OriginalUrlHash = new byte[32],
        IsActive = true,
    };

    private async Task<ResolvedShortCode?> ResolveAsync(string code, ILogger<ShortCodeResolver>? logger = null)
    {
        await using var context = CreateContext();
        var resolver = new ShortCodeResolver(
            context,
            _cache,
            _timeProvider,
            Options.Create(new RedirectCacheOptions { MaxTimeToLive = MaxTimeToLive }),
            logger ?? NullLogger<ShortCodeResolver>.Instance);
        return await resolver.ResolveAsync(code, default);
    }

    private async Task<UrlMapping> SeedAsync(params UrlMapping[] mappings)
    {
        await using var context = CreateContext();
        context.UrlMappings.AddRange(mappings);
        await context.SaveChangesAsync();
        return mappings[0];
    }

    private async Task RemoveFromSqlAsync(string code)
    {
        await using var context = CreateContext();
        context.UrlMappings.Remove(await context.UrlMappings.SingleAsync(m => m.ShortCode == code));
        await context.SaveChangesAsync();
    }

    private UrlShortenerDbContext CreateContext() => new(
        new DbContextOptionsBuilder<UrlShortenerDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .AddInterceptors(new InMemoryRowVersionInterceptor())
            .Options,
        _timeProvider);
}
