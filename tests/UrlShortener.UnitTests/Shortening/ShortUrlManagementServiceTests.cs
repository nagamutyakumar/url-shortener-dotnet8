using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Persistence.Entities;
using UrlShortener.Api.Shortening;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Shortening;

// Uses the EF Core InMemory provider. Its string comparison is always ordinal and it checks
// concurrency tokens on update; InMemoryRowVersionInterceptor stands in for SQL Server's
// rowversion generation. See docs/integration-test-plan.md for what still needs SQL Server.
public sealed class ShortUrlManagementServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Created = Now.UtcDateTime.AddDays(-1);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FixedTimeProvider _timeProvider = new(Now);
    private readonly BeforeSaveChangesInterceptor _beforeSave = new();

    [Fact]
    public async Task UpdateAsync_ReplacesCanonicalUrlAndHash_KeepsCodeIdAndCreatedAt()
    {
        var seeded = await SeedAsync(Mapping("Abc1234"));

        var outcome = await UpdateAsync("Abc1234", Request("HTTPS://Example.com:443/New?q=1"));

        var updated = Assert.IsType<ManagementOutcome.Updated>(outcome);
        Assert.Equal("Abc1234", updated.Mapping.ShortCode);
        var stored = await ReadAsync("Abc1234");
        Assert.Equal(seeded.Id, stored.Id);
        Assert.Equal("https://example.com/New?q=1", stored.OriginalUrl);
        Assert.Equal(UrlCanonicalizer.ComputeHash("https://example.com/New?q=1"), stored.OriginalUrlHash);
        Assert.Equal(Created, stored.CreatedAtUtc);
        Assert.Equal(Now.UtcDateTime, stored.UpdatedAtUtc);
        Assert.NotEqual(seeded.RowVersion, stored.RowVersion);
        Assert.True(stored.IsActive);
    }

    [Fact]
    public async Task UpdateAsync_OmittedExpiration_PreservesCurrentValue()
    {
        var expires = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(Mapping("Abc1234", expiresAtUtc: expires));

        await UpdateAsync("Abc1234", Request("https://example.com/new"));

        Assert.Equal(expires, (await ReadAsync("Abc1234")).ExpiresAtUtc);
    }

    [Fact]
    public async Task UpdateAsync_ExplicitNullExpiration_ClearsIt()
    {
        await SeedAsync(Mapping("Abc1234", expiresAtUtc: new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        await UpdateAsync("Abc1234", Request("https://example.com/new", expiresAtUtc: null));

        Assert.Null((await ReadAsync("Abc1234")).ExpiresAtUtc);
    }

    [Fact]
    public async Task UpdateAsync_FutureExpiration_IsConvertedToUtcAndTruncatedToMilliseconds()
    {
        await SeedAsync(Mapping("Abc1234"));

        await UpdateAsync(
            "Abc1234",
            Request("https://example.com/new", new DateTimeOffset(2030, 1, 1, 5, 30, 0, TimeSpan.FromHours(5.5)).AddTicks(1_239_000)));

        Assert.Equal(
            new DateTime(2030, 1, 1, 0, 0, 0, 123, DateTimeKind.Utc),
            (await ReadAsync("Abc1234")).ExpiresAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task UpdateAsync_NonFutureExpiration_IsInvalidAndChangesNothing(int offsetMinutes)
    {
        await SeedAsync(Mapping("Abc1234"));

        var outcome = await UpdateAsync("Abc1234", Request("https://example.com/new", Now.AddMinutes(offsetMinutes)));

        var invalid = Assert.IsType<ManagementOutcome.Invalid>(outcome);
        Assert.Equal(new[] { nameof(UpdateShortUrlRequest.ExpiresAtUtc) }, invalid.Errors.Keys);
        Assert.Equal("https://example.com/old", (await ReadAsync("Abc1234")).OriginalUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://example.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@example.com/")]
    public async Task UpdateAsync_InvalidUrl_IsInvalidAndChangesNothing(string? url)
    {
        await SeedAsync(Mapping("Abc1234"));

        var outcome = await UpdateAsync("Abc1234", Request(url));

        var invalid = Assert.IsType<ManagementOutcome.Invalid>(outcome);
        Assert.Equal(new[] { nameof(UpdateShortUrlRequest.OriginalUrl) }, invalid.Errors.Keys);
        Assert.Equal("https://example.com/old", (await ReadAsync("Abc1234")).OriginalUrl);
    }

    [Fact]
    public async Task UpdateAsync_TooLongUrl_IsInvalid()
    {
        await SeedAsync(Mapping("Abc1234"));

        var outcome = await UpdateAsync(
            "Abc1234", Request("https://example.com/" + new string('a', UrlCanonicalizer.MaxLength)));

        Assert.IsType<ManagementOutcome.Invalid>(outcome);
    }

    [Theory]
    [InlineData("abc1234")]
    [InlineData("ABC1234")]
    [InlineData("Zzz9999")]
    [InlineData("Abc123")]
    [InlineData("Abc-123")]
    public async Task UpdateAsync_UnknownMalformedOrWrongCaseCode_IsNotFoundAndChangesNothing(string code)
    {
        await SeedAsync(Mapping("Abc1234"));

        var outcome = await UpdateAsync(code, Request("https://example.com/new"));

        Assert.IsType<ManagementOutcome.NotFound>(outcome);
        Assert.Equal("https://example.com/old", (await ReadAsync("Abc1234")).OriginalUrl);
    }

    [Fact]
    public async Task UpdateAsync_CodesDifferingOnlyInCase_UpdatesOnlyTheExactMatch()
    {
        await SeedAsync(Mapping("abc1234", "https://example.com/lower"), Mapping("ABC1234", "https://example.com/upper"));

        await UpdateAsync("ABC1234", Request("https://example.com/new"));

        Assert.Equal("https://example.com/lower", (await ReadAsync("abc1234")).OriginalUrl);
        Assert.Equal("https://example.com/new", (await ReadAsync("ABC1234")).OriginalUrl);
    }

    [Fact]
    public async Task UpdateAsync_InactiveMapping_IsNotFoundAndChangesNothing()
    {
        await SeedAsync(Mapping("Abc1234", isActive: false));

        var outcome = await UpdateAsync("Abc1234", Request("https://example.com/new", expiresAtUtc: null));

        Assert.IsType<ManagementOutcome.NotFound>(outcome);
        var stored = await ReadAsync("Abc1234");
        Assert.False(stored.IsActive);
        Assert.Equal("https://example.com/old", stored.OriginalUrl);
    }

    [Fact]
    public async Task UpdateAsync_ExpiredMapping_CanBeUpdatedAndGivenANewExpiration()
    {
        await SeedAsync(Mapping("Abc1234", expiresAtUtc: Now.UtcDateTime.AddDays(-1)));
        var newExpiry = Now.AddDays(30);

        var outcome = await UpdateAsync("Abc1234", Request("https://example.com/new", newExpiry));

        Assert.IsType<ManagementOutcome.Updated>(outcome);
        Assert.Equal(newExpiry.UtcDateTime, (await ReadAsync("Abc1234")).ExpiresAtUtc);
    }

    [Fact]
    public async Task UpdateAsync_ExpiredMappingWithOmittedExpiration_StaysExpired()
    {
        var expired = Now.UtcDateTime.AddDays(-1);
        await SeedAsync(Mapping("Abc1234", expiresAtUtc: expired));

        var outcome = await UpdateAsync("Abc1234", Request("https://example.com/new"));

        Assert.IsType<ManagementOutcome.Updated>(outcome);
        Assert.Equal(expired, (await ReadAsync("Abc1234")).ExpiresAtUtc);
    }

    [Fact]
    public async Task UpdateAsync_UnchangedValues_DoesNotWrite()
    {
        var seeded = await SeedAsync(Mapping("Abc1234"));

        var outcome = await UpdateAsync("Abc1234", Request("https://example.com/old"));

        Assert.IsType<ManagementOutcome.Updated>(outcome);
        var stored = await ReadAsync("Abc1234");
        Assert.Equal(seeded.RowVersion, stored.RowVersion);
        Assert.Equal(Created, stored.UpdatedAtUtc);
    }

    [Fact]
    public async Task UpdateAsync_KeepsClickHistoryAttached()
    {
        var seeded = await SeedAsync(Mapping("Abc1234"));
        await SeedClicksAsync(seeded.Id, 3);

        await UpdateAsync("Abc1234", Request("https://example.com/new"));

        await using var context = CreateContext();
        Assert.Equal(3, await context.ClickEvents.CountAsync(e => e.UrlMappingId == seeded.Id));
    }

    [Fact]
    public async Task UpdateAsync_RowChangedAfterRead_ReturnsConflictAndKeepsOtherWritersChange()
    {
        await SeedAsync(Mapping("Abc1234"));
        _beforeSave.RunOnce(() => ConcurrentlyAsync(m => m.OriginalUrl = "https://example.com/other-writer"));

        var outcome = await UpdateAsync("Abc1234", Request("https://example.com/new"));

        Assert.IsType<ManagementOutcome.Conflict>(outcome);
        Assert.Equal("https://example.com/other-writer", (await ReadAsync("Abc1234")).OriginalUrl);
    }

    [Fact]
    public async Task UpdateAsync_DeletedAfterRead_ReturnsConflictAndDoesNotRevive()
    {
        await SeedAsync(Mapping("Abc1234"));
        _beforeSave.RunOnce(() => ConcurrentlyAsync(m => m.IsActive = false));

        var outcome = await UpdateAsync("Abc1234", Request("https://example.com/new"));

        Assert.IsType<ManagementOutcome.Conflict>(outcome);
        var stored = await ReadAsync("Abc1234");
        Assert.False(stored.IsActive);
        Assert.Equal("https://example.com/old", stored.OriginalUrl);
    }

    [Fact]
    public async Task DeleteAsync_ActiveMapping_SetsInactiveAndKeepsRowAndClicks()
    {
        var seeded = await SeedAsync(Mapping("Abc1234"));
        await SeedClicksAsync(seeded.Id, 2);

        var outcome = await DeleteAsync("Abc1234");

        Assert.IsType<ManagementOutcome.Deleted>(outcome);
        var stored = await ReadAsync("Abc1234");
        Assert.False(stored.IsActive);
        Assert.Equal("https://example.com/old", stored.OriginalUrl);
        Assert.Equal(Now.UtcDateTime, stored.UpdatedAtUtc);
        await using var context = CreateContext();
        Assert.Equal(2, await context.ClickEvents.CountAsync(e => e.UrlMappingId == seeded.Id));
    }

    [Fact]
    public async Task DeleteAsync_AlreadyInactive_SucceedsWithoutWriting()
    {
        var seeded = await SeedAsync(Mapping("Abc1234", isActive: false));

        var outcome = await DeleteAsync("Abc1234");

        Assert.IsType<ManagementOutcome.Deleted>(outcome);
        var stored = await ReadAsync("Abc1234");
        Assert.Equal(seeded.RowVersion, stored.RowVersion);
        Assert.Equal(Created, stored.UpdatedAtUtc);
    }

    [Fact]
    public async Task DeleteAsync_ExpiredMapping_IsDeleted()
    {
        await SeedAsync(Mapping("Abc1234", expiresAtUtc: Now.UtcDateTime.AddDays(-1)));

        Assert.IsType<ManagementOutcome.Deleted>(await DeleteAsync("Abc1234"));
        Assert.False((await ReadAsync("Abc1234")).IsActive);
    }

    [Theory]
    [InlineData("abc1234")]
    [InlineData("Zzz9999")]
    [InlineData("Abc12345")]
    [InlineData("Abc_123")]
    public async Task DeleteAsync_UnknownMalformedOrWrongCaseCode_IsNotFoundAndChangesNothing(string code)
    {
        await SeedAsync(Mapping("Abc1234"));

        Assert.IsType<ManagementOutcome.NotFound>(await DeleteAsync(code));
        Assert.True((await ReadAsync("Abc1234")).IsActive);
    }

    [Fact]
    public async Task DeleteAsync_RowChangedAfterRead_ReturnsConflictAndKeepsOtherWritersChange()
    {
        await SeedAsync(Mapping("Abc1234"));
        _beforeSave.RunOnce(() => ConcurrentlyAsync(m => m.OriginalUrl = "https://example.com/other-writer"));

        var outcome = await DeleteAsync("Abc1234");

        Assert.IsType<ManagementOutcome.Conflict>(outcome);
        var stored = await ReadAsync("Abc1234");
        Assert.True(stored.IsActive);
        Assert.Equal("https://example.com/other-writer", stored.OriginalUrl);
    }

    private static UpdateShortUrlRequest Request(string? originalUrl) => new() { OriginalUrl = originalUrl };

    private static UpdateShortUrlRequest Request(string? originalUrl, DateTimeOffset? expiresAtUtc) =>
        new() { OriginalUrl = originalUrl, ExpiresAtUtc = expiresAtUtc };

    private static UrlMapping Mapping(
        string shortCode,
        string originalUrl = "https://example.com/old",
        DateTime? expiresAtUtc = null,
        bool isActive = true) => new()
    {
        ShortCode = shortCode,
        OriginalUrl = originalUrl,
        OriginalUrlHash = UrlCanonicalizer.ComputeHash(originalUrl),
        ExpiresAtUtc = expiresAtUtc,
        IsActive = isActive,
    };

    private async Task<ManagementOutcome> UpdateAsync(string code, UpdateShortUrlRequest request)
    {
        await using var context = CreateContext(_beforeSave);
        return await CreateService(context).UpdateAsync(code, request, default);
    }

    private async Task<ManagementOutcome> DeleteAsync(string code)
    {
        await using var context = CreateContext(_beforeSave);
        return await CreateService(context).DeleteAsync(code, default);
    }

    private ShortUrlManagementService CreateService(UrlShortenerDbContext context) =>
        new(context, _timeProvider, NullLogger<ShortUrlManagementService>.Instance);

    /// <summary>Another request's write, through its own context, committed before ours.</summary>
    private async Task ConcurrentlyAsync(Action<UrlMapping> change)
    {
        await using var context = CreateContext();
        var mapping = await context.UrlMappings.SingleAsync(m => m.ShortCode == "Abc1234");
        change(mapping);
        await context.SaveChangesAsync();
    }

    /// <summary>Seeded with CreatedAtUtc/UpdatedAtUtc one day before "now".</summary>
    private async Task<UrlMapping> SeedAsync(params UrlMapping[] mappings)
    {
        await using var context = CreateContext(clock: new FixedTimeProvider(Created));
        context.UrlMappings.AddRange(mappings);
        await context.SaveChangesAsync();
        return mappings[0];
    }

    private async Task SeedClicksAsync(long urlMappingId, int count)
    {
        await using var context = CreateContext();
        for (var i = 0; i < count; i++)
        {
            context.ClickEvents.Add(new ClickEvent { UrlMappingId = urlMappingId, ClickedAtUtc = Created });
        }

        await context.SaveChangesAsync();
    }

    private async Task<UrlMapping> ReadAsync(string code)
    {
        await using var context = CreateContext();
        return await context.UrlMappings.AsNoTracking().SingleAsync(m => m.ShortCode == code);
    }

    private UrlShortenerDbContext CreateContext(
        BeforeSaveChangesInterceptor? beforeSave = null,
        TimeProvider? clock = null)
    {
        var builder = new DbContextOptionsBuilder<UrlShortenerDbContext>().UseInMemoryDatabase(_databaseName);
        builder = beforeSave is null
            ? builder.AddInterceptors(new InMemoryRowVersionInterceptor())
            : builder.AddInterceptors(beforeSave, new InMemoryRowVersionInterceptor());
        return new UrlShortenerDbContext(builder.Options, clock ?? _timeProvider);
    }
}
