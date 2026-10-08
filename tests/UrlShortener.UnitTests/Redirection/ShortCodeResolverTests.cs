using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Persistence.Entities;
using UrlShortener.Api.Redirection;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Redirection;

// Uses the EF Core InMemory provider, whose string comparison is always ordinal. Case-sensitive
// matching in SQL Server depends on the ShortCode column's binary collation; see
// docs/integration-test-plan.md.
public sealed class ShortCodeResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FixedTimeProvider _timeProvider = new(Now);

    [Fact]
    public async Task ResolveAsync_ActiveMappingWithoutExpiration_ReturnsOriginalUrl()
    {
        await SeedAsync(Mapping("Abc1234", "https://example.com/Path?q=1"));

        Assert.Equal("https://example.com/Path?q=1", await ResolveAsync("Abc1234"));
    }

    [Fact]
    public async Task ResolveAsync_ReturnsMappingIdForClickTracking()
    {
        var mapping = Mapping("Abc1234", "https://example.com/");
        await SeedAsync(Mapping("Other01", "https://example.com/other"), mapping);
        await using var context = CreateContext();

        var resolved = await new ShortCodeResolver(context, _timeProvider).ResolveAsync("Abc1234", default);

        Assert.Equal(new ResolvedShortCode(mapping.Id, "https://example.com/"), resolved);
    }

    [Fact]
    public async Task ResolveAsync_UnknownCode_ReturnsNull()
    {
        await SeedAsync(Mapping("Abc1234", "https://example.com/"));

        Assert.Null(await ResolveAsync("Zzz9999"));
    }

    [Fact]
    public async Task ResolveAsync_InactiveMapping_ReturnsNull()
    {
        var mapping = Mapping("Abc1234", "https://example.com/");
        mapping.IsActive = false;
        await SeedAsync(mapping);

        Assert.Null(await ResolveAsync("Abc1234"));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task ResolveAsync_ExpiresExactlyAtNowIsExpired(int expiresOffsetMilliseconds, bool resolves)
    {
        var mapping = Mapping("Abc1234", "https://example.com/");
        mapping.ExpiresAtUtc = Now.UtcDateTime.AddMilliseconds(expiresOffsetMilliseconds);
        await SeedAsync(mapping);

        var result = await ResolveAsync("Abc1234");

        Assert.Equal(resolves ? "https://example.com/" : null, result);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task ResolveAsync_SubMillisecondClock_KeepsBoundaryExact(int expiresOffsetMilliseconds, bool resolves)
    {
        // The clock is 0.5 ms past Now; stored expirations are whole milliseconds (datetime2(3)).
        _timeProvider.UtcNow = Now.AddTicks(TimeSpan.TicksPerMillisecond / 2);
        var mapping = Mapping("Abc1234", "https://example.com/");
        mapping.ExpiresAtUtc = Now.UtcDateTime.AddMilliseconds(expiresOffsetMilliseconds);
        await SeedAsync(mapping);

        var result = await ResolveAsync("Abc1234");

        Assert.Equal(resolves ? "https://example.com/" : null, result);
    }

    [Theory]
    [InlineData("abc1234")]
    [InlineData("ABC1234")]
    [InlineData("aBC1234")]
    public async Task ResolveAsync_DifferentCase_ReturnsNull(string code)
    {
        await SeedAsync(Mapping("Abc1234", "https://example.com/"));

        Assert.Null(await ResolveAsync(code));
    }

    [Fact]
    public async Task ResolveAsync_CodesDifferingOnlyByCase_ResolveIndependently()
    {
        await SeedAsync(
            Mapping("abc1234", "https://example.com/lower"),
            Mapping("ABC1234", "https://example.com/upper"));

        Assert.Equal("https://example.com/lower", await ResolveAsync("abc1234"));
        Assert.Equal("https://example.com/upper", await ResolveAsync("ABC1234"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Abc123")]
    [InlineData("Abc12345")]
    [InlineData("Abc-123")]
    [InlineData("Abc_123")]
    [InlineData("Abc 123")]
    [InlineData("Abc123 ")]
    [InlineData("Abc%123")]
    [InlineData("Abc/123")]
    [InlineData("Abc123é")]
    public async Task ResolveAsync_InvalidFormat_ReturnsNullWithoutQuerying(string code)
    {
        // Any database access through a disposed context throws ObjectDisposedException.
        var context = CreateContext();
        await context.DisposeAsync();
        var resolver = new ShortCodeResolver(context, _timeProvider);

        Assert.Null(await resolver.ResolveAsync(code, default));
    }

    [Fact]
    public async Task ResolveAsync_CanceledToken_Throws()
    {
        await SeedAsync(Mapping("Abc1234", "https://example.com/"));
        await using var context = CreateContext();
        var resolver = new ShortCodeResolver(context, _timeProvider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync("Abc1234", new CancellationToken(canceled: true)));
    }

    private static UrlMapping Mapping(string shortCode, string originalUrl) => new()
    {
        ShortCode = shortCode,
        OriginalUrl = originalUrl,
        OriginalUrlHash = new byte[32],
        IsActive = true,
    };

    private async Task<string?> ResolveAsync(string code)
    {
        await using var context = CreateContext();
        return (await new ShortCodeResolver(context, _timeProvider).ResolveAsync(code, default))?.OriginalUrl;
    }

    private UrlShortenerDbContext CreateContext() => new(
        new DbContextOptionsBuilder<UrlShortenerDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .AddInterceptors(new InMemoryRowVersionInterceptor())
            .Options,
        _timeProvider);

    private async Task SeedAsync(params UrlMapping[] mappings)
    {
        await using var context = CreateContext();
        context.UrlMappings.AddRange(mappings);
        await context.SaveChangesAsync();
    }
}
