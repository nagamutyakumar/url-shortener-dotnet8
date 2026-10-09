using System.Text.Json;
using StackExchange.Redis;

namespace UrlShortener.Api.Caching;

/// <summary>
/// One Redis string per short code, holding a small JSON <see cref="CachedRedirect"/> with a TTL.
/// </summary>
/// <remarks>
/// While the multiplexer is disconnected, lookups and fills are skipped (a miss) instead of
/// throwing on every redirect; the outage is logged once by the connection events registered in
/// <see cref="RedirectCacheServiceCollectionExtensions"/>. Removal always attempts the command
/// and throws on failure, because a lost invalidation must be reported.
/// </remarks>
public sealed class RedisRedirectCache : IRedirectCache
{
    /// <summary>"v1" lets a future value format use new keys instead of misreading old entries.</summary>
    public const string KeyPrefix = "urlshortener:redirect:v1:";

    private readonly IConnectionMultiplexer _multiplexer;

    public RedisRedirectCache(IConnectionMultiplexer multiplexer)
    {
        _multiplexer = multiplexer;
    }

    /// <summary>
    /// Redis keys are binary-safe and compared exactly, so codes differing only in case never
    /// share an entry. Callers pass only codes that passed <c>ShortCodeFormat.IsValid</c>.
    /// </summary>
    public static string KeyFor(string shortCode) => KeyPrefix + shortCode;

    public async Task<CachedRedirect?> GetAsync(string shortCode)
    {
        if (!_multiplexer.IsConnected)
        {
            return null;
        }

        var value = await _multiplexer.GetDatabase().StringGetAsync(KeyFor(shortCode));
        return value.IsNullOrEmpty ? null : Deserialize(value!);
    }

    public Task SetAsync(string shortCode, CachedRedirect entry, TimeSpan timeToLive)
    {
        if (!_multiplexer.IsConnected)
        {
            return Task.CompletedTask;
        }

        return _multiplexer.GetDatabase().StringSetAsync(KeyFor(shortCode), Serialize(entry), timeToLive);
    }

    public Task RemoveAsync(string shortCode) =>
        _multiplexer.GetDatabase().KeyDeleteAsync(KeyFor(shortCode));

    public static string Serialize(CachedRedirect entry) => JsonSerializer.Serialize(entry);

    /// <returns>Null (treated as a miss) for anything that is not a well-formed entry.</returns>
    public static CachedRedirect? Deserialize(string value)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<CachedRedirect>(value);
            return entry is { UrlMappingId: > 0, OriginalUrl.Length: > 0 }
                && entry.ExpiresAtUtc is null or { Kind: DateTimeKind.Utc }
                ? entry
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
