using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Persistence.Entities;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Analytics;

// InMemory proves the aggregation semantics, not the SQL: see ClickAnalyticsSqlTranslationTests
// and docs/integration-test-plan.md.
public sealed class ClickAnalyticsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FixedTimeProvider _timeProvider = new(Now);

    [Theory]
    [InlineData("Zzz9999")]
    [InlineData("abc1234")]
    [InlineData("ABC1234")]
    [InlineData("Abc123")]
    [InlineData("Abc-123")]
    [InlineData("")]
    public async Task UnknownMalformedOrWrongCaseCode_ReturnsNull(string code)
    {
        await SeedMappingAsync("Abc1234");

        Assert.Null(await GetAsync(code));
    }

    [Fact]
    public async Task NoClicks_ReportsGenuineZeros()
    {
        await SeedMappingAsync("Abc1234");

        var result = await GetAsync("Abc1234");

        Assert.NotNull(result);
        Assert.Equal("Abc1234", result.ShortCode);
        Assert.Equal(LinkStatuses.Active, result.LinkStatus);
        Assert.Equal(0, result.TotalClicks);
        Assert.Equal(new UniqueVisitors(UniqueVisitorStatuses.Complete, 0, 0), result.UniqueVisitors);
        Assert.Empty(result.Countries);
        Assert.Equal(0, result.ClicksWithUnknownCountry);
    }

    // Every hash here is different. The InMemory provider compares byte[] values by reference
    // in Distinct(), so it cannot prove that equal hashes are merged; SQL Server's
    // COUNT_BIG(DISTINCT [VisitorHash]) compares binary(32) by value. That merge is covered by
    // the SQL translation test and integration scenario A3.
    [Fact]
    public async Task AllClicksHashed_ReportsCompleteCount()
    {
        var mapping = await SeedMappingAsync("Abc1234");
        await SeedClicksAsync(Click(mapping, Hash(1)), Click(mapping, Hash(2)), Click(mapping, Hash(3)));

        var result = (await GetAsync("Abc1234"))!;

        Assert.Equal(3, result.TotalClicks);
        Assert.Equal(new UniqueVisitors(UniqueVisitorStatuses.Complete, 3, 3), result.UniqueVisitors);
    }

    [Fact]
    public async Task SomeClicksHashed_ReportsPartialCountAndCoverage()
    {
        var mapping = await SeedMappingAsync("Abc1234");
        await SeedClicksAsync(
            Click(mapping, Hash(1)),
            Click(mapping, Hash(2)),
            Click(mapping, null),
            Click(mapping, null),
            Click(mapping, null));

        var result = (await GetAsync("Abc1234"))!;

        Assert.Equal(5, result.TotalClicks);
        Assert.Equal(new UniqueVisitors(UniqueVisitorStatuses.Partial, 2, 2), result.UniqueVisitors);
    }

    [Theory]
    [InlineData(0, 0, 0, UniqueVisitorStatuses.Complete, 0L)]
    [InlineData(10, 10, 4, UniqueVisitorStatuses.Complete, 4L)]
    [InlineData(10, 6, 2, UniqueVisitorStatuses.Partial, 2L)]
    [InlineData(10, 0, 0, UniqueVisitorStatuses.Unavailable, null)]
    public void ToUniqueVisitors_MapsCoverageToStatus(
        long total, long hashed, long distinct, string expectedStatus, long? expectedCount)
    {
        var result = ClickAnalyticsService.ToUniqueVisitors(new ClickSummary(total, hashed, distinct, total));

        Assert.Equal(new UniqueVisitors(expectedStatus, expectedCount, hashed), result);
    }

    [Fact]
    public async Task NoClicksHashed_ReportsUnavailableNotZero()
    {
        var mapping = await SeedMappingAsync("Abc1234");
        await SeedClicksAsync(Click(mapping, null), Click(mapping, null));

        var result = (await GetAsync("Abc1234"))!;

        Assert.Equal(2, result.TotalClicks);
        Assert.Equal(new UniqueVisitors(UniqueVisitorStatuses.Unavailable, null, 0), result.UniqueVisitors);
    }

    [Fact]
    public async Task Countries_OnlyStoredCodes_OrderedByClicksThenCode()
    {
        var mapping = await SeedMappingAsync("Abc1234");
        await SeedClicksAsync(
            Click(mapping, null, "FR"),
            Click(mapping, null, "DE"),
            Click(mapping, null, "DE"),
            Click(mapping, null, "BE"),
            Click(mapping, null, null),
            Click(mapping, null, null),
            Click(mapping, null, null));

        var result = (await GetAsync("Abc1234"))!;

        Assert.Equal(
            new[] { new CountryClicks("DE", 2), new CountryClicks("BE", 1), new CountryClicks("FR", 1) },
            result.Countries);
        Assert.Equal(3, result.ClicksWithUnknownCountry);
        Assert.Equal(7, result.TotalClicks);
    }

    [Fact]
    public async Task OtherMappingsClicks_AreExcluded()
    {
        var mapping = await SeedMappingAsync("Abc1234");
        var other = await SeedMappingAsync("Other01");
        await SeedClicksAsync(
            Click(mapping, Hash(1), "DE"),
            Click(other, Hash(3), "DE"),
            Click(other, Hash(2), "FR"));

        var result = (await GetAsync("Abc1234"))!;

        Assert.Equal(1, result.TotalClicks);
        Assert.Equal(new UniqueVisitors(UniqueVisitorStatuses.Complete, 1, 1), result.UniqueVisitors);
        Assert.Equal(new[] { new CountryClicks("DE", 1) }, result.Countries);
    }

    [Fact]
    public async Task CodesDifferingOnlyByCase_HaveSeparateAnalytics()
    {
        var lower = await SeedMappingAsync("abc1234");
        await SeedMappingAsync("ABC1234");
        await SeedClicksAsync(Click(lower, null), Click(lower, null));

        Assert.Equal(2, (await GetAsync("abc1234"))!.TotalClicks);
        Assert.Equal(0, (await GetAsync("ABC1234"))!.TotalClicks);
    }

    [Theory]
    [InlineData(true, null, LinkStatuses.Active)]
    [InlineData(true, 1, LinkStatuses.Active)]
    [InlineData(true, 0, LinkStatuses.Expired)]
    [InlineData(true, -1, LinkStatuses.Expired)]
    [InlineData(false, null, LinkStatuses.Inactive)]
    [InlineData(false, -1, LinkStatuses.Inactive)]
    public async Task InactiveAndExpiredMappings_StillReturnAnalytics(
        bool isActive, int? expiresOffsetMilliseconds, string expectedStatus)
    {
        var mapping = await SeedMappingAsync(
            "Abc1234",
            isActive,
            expiresOffsetMilliseconds is { } ms ? Now.UtcDateTime.AddMilliseconds(ms) : null);
        await SeedClicksAsync(Click(mapping, null));

        var result = (await GetAsync("Abc1234"))!;

        Assert.Equal(expectedStatus, result.LinkStatus);
        Assert.Equal(mapping.ExpiresAtUtc, result.ExpiresAtUtc);
        Assert.Equal(1, result.TotalClicks);
    }

    [Fact]
    public async Task CanceledToken_Throws()
    {
        await SeedMappingAsync("Abc1234");
        await using var context = CreateContext();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ClickAnalyticsService(context, _timeProvider).GetAsync("Abc1234", new CancellationToken(true)));
    }

    private async Task<ClickAnalyticsResponse?> GetAsync(string code)
    {
        await using var context = CreateContext();
        return await new ClickAnalyticsService(context, _timeProvider).GetAsync(code, default);
    }

    private UrlShortenerDbContext CreateContext() => new(
        new DbContextOptionsBuilder<UrlShortenerDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .AddInterceptors(new InMemoryRowVersionInterceptor())
            .Options,
        _timeProvider);

    private async Task<UrlMapping> SeedMappingAsync(string shortCode, bool isActive = true, DateTime? expiresAtUtc = null)
    {
        var mapping = new UrlMapping
        {
            ShortCode = shortCode,
            OriginalUrl = "https://example.com/",
            OriginalUrlHash = new byte[32],
            IsActive = isActive,
            ExpiresAtUtc = expiresAtUtc,
        };
        await using var context = CreateContext();
        context.UrlMappings.Add(mapping);
        await context.SaveChangesAsync();
        return mapping;
    }

    private async Task SeedClicksAsync(params ClickEvent[] clicks)
    {
        await using var context = CreateContext();
        context.ClickEvents.AddRange(clicks);
        await context.SaveChangesAsync();
    }

    private static ClickEvent Click(UrlMapping mapping, byte[]? visitorHash, string? countryCode = null) => new()
    {
        UrlMappingId = mapping.Id,
        ClickedAtUtc = Now.UtcDateTime,
        VisitorHash = visitorHash,
        CountryCode = countryCode,
    };

    private static byte[] Hash(byte seed)
    {
        var hash = new byte[32];
        Array.Fill(hash, seed);
        return hash;
    }
}
