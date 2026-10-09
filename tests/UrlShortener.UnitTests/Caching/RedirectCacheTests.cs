using UrlShortener.Api.Caching;

namespace UrlShortener.UnitTests.Caching;

// Pure logic behind the Redis cache: TTL bounds, options validation, key format and the stored
// value format. The Redis commands themselves need a live server; see docs/integration-test-plan.md.
public sealed class RedirectCacheTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(5);

    [Fact]
    public void GetTimeToLive_NoExpiration_UsesMax()
    {
        Assert.Equal(Max, RedirectCacheOptions.GetTimeToLive(null, Now, Max));
    }

    [Fact]
    public void GetTimeToLive_ExpiresAfterMax_UsesMax()
    {
        Assert.Equal(Max, RedirectCacheOptions.GetTimeToLive(Now.AddHours(1), Now, Max));
    }

    [Fact]
    public void GetTimeToLive_ExpiresBeforeMax_UsesRemainingLifetime()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RedirectCacheOptions.GetTimeToLive(Now.AddSeconds(30), Now, Max));
        Assert.Equal(TimeSpan.FromMilliseconds(1), RedirectCacheOptions.GetTimeToLive(Now.AddMilliseconds(1), Now, Max));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-60_000)]
    public void GetTimeToLive_ExpiredOrExpiringNow_IsNotCached(int offsetMilliseconds)
    {
        Assert.Null(RedirectCacheOptions.GetTimeToLive(Now.AddMilliseconds(offsetMilliseconds), Now, Max));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(300, true)]
    [InlineData(86_400, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(86_401, false)]
    public void Options_MaxTimeToLiveMustBeBounded(int seconds, bool valid)
    {
        var options = new RedirectCacheOptions { MaxTimeToLive = TimeSpan.FromSeconds(seconds) };

        Assert.Equal(valid, RedirectCacheOptions.IsValid(options));
    }

    [Fact]
    public void Options_DefaultIsFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), new RedirectCacheOptions().MaxTimeToLive);
    }

    [Fact]
    public void KeyFor_IsPrefixedVersionedAndCaseSensitive()
    {
        Assert.Equal("urlshortener:redirect:v1:Abc1234", RedisRedirectCache.KeyFor("Abc1234"));
        Assert.NotEqual(RedisRedirectCache.KeyFor("abc1234"), RedisRedirectCache.KeyFor("Abc1234"));
    }

    [Fact]
    public void Serialize_RoundTripsIdUrlAndUtcExpiration()
    {
        var entry = new CachedRedirect(42, "https://example.com/a?q=%C3%A9#f", new DateTime(2030, 1, 1, 0, 0, 0, 123, DateTimeKind.Utc));

        var json = RedisRedirectCache.Serialize(entry);
        var roundTripped = RedisRedirectCache.Deserialize(json);

        Assert.Equal(entry, roundTripped);
        Assert.Equal(DateTimeKind.Utc, roundTripped!.ExpiresAtUtc!.Value.Kind);
    }

    [Fact]
    public void Serialize_RoundTripsNullExpiration()
    {
        var entry = new CachedRedirect(42, "https://example.com/", null);

        Assert.Equal(entry, RedisRedirectCache.Deserialize(RedisRedirectCache.Serialize(entry)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"UrlMappingId":0,"OriginalUrl":"https://example.com/","ExpiresAtUtc":null}""")]
    [InlineData("""{"UrlMappingId":1,"OriginalUrl":"","ExpiresAtUtc":null}""")]
    [InlineData("""{"UrlMappingId":1,"ExpiresAtUtc":null}""")]
    [InlineData("""{"UrlMappingId":1,"OriginalUrl":"https://example.com/","ExpiresAtUtc":"2030-01-01T00:00:00"}""")]
    [InlineData("""{"UrlMappingId":"x","OriginalUrl":"https://example.com/","ExpiresAtUtc":null}""")]
    public void Deserialize_MalformedValue_IsTreatedAsMiss(string value)
    {
        Assert.Null(RedisRedirectCache.Deserialize(value));
    }

    [Fact]
    public void CreateConfigurationOptions_ForcesNonFatalFailFastBehaviour()
    {
        var options = RedirectCacheServiceCollectionExtensions.CreateConfigurationOptions(
            "localhost:6379,abortConnect=true,asyncTimeout=500");

        Assert.False(options.AbortOnConnectFail);
        Assert.False(options.BacklogPolicy.QueueWhileDisconnected);
        Assert.Equal(500, options.AsyncTimeout);
    }
}
