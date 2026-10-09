using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Caching;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.ShortCodes;

namespace UrlShortener.Api.Redirection;

public sealed record ResolvedShortCode(long UrlMappingId, string OriginalUrl);

/// <summary>
/// Resolves a short code to the URL it redirects to.
/// </summary>
/// <remarks>
/// <para>A code resolves only when it is well formed, matches a stored code exactly (case-sensitive),
/// and the mapping is active and not expired. Expired means <c>ExpiresAtUtc &lt;= now</c>.
/// The destination URL is never contacted.</para>
/// <para>Cache-aside: a cache hit is used only while its stored expiration is in the future;
/// otherwise SQL Server is queried and, when the code resolves there, the entry is (re)cached with
/// a TTL no longer than <see cref="RedirectCacheOptions.MaxTimeToLive"/> or the link's remaining
/// lifetime. Codes that do not resolve are never cached. Cache failures are logged and treated as
/// misses, so redirects keep working from SQL Server. See docs/caching.md.</para>
/// </remarks>
public sealed class ShortCodeResolver
{
    private readonly UrlShortenerDbContext _dbContext;
    private readonly IRedirectCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _maxTimeToLive;
    private readonly ILogger<ShortCodeResolver> _logger;

    public ShortCodeResolver(
        UrlShortenerDbContext dbContext,
        IRedirectCache cache,
        TimeProvider timeProvider,
        IOptions<RedirectCacheOptions> cacheOptions,
        ILogger<ShortCodeResolver> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _timeProvider = timeProvider;
        _maxTimeToLive = cacheOptions.Value.MaxTimeToLive;
        _logger = logger;
    }

    /// <returns>The mapping id and original URL, or null when the code does not resolve.</returns>
    public async Task<ResolvedShortCode?> ResolveAsync(string code, CancellationToken cancellationToken)
    {
        // Malformed codes can never match, so they are rejected without a cache or SQL lookup.
        if (!ShortCodeFormat.IsValid(code))
        {
            return null;
        }

        // ExpiresAtUtc is stored as datetime2(3), so it is always a whole millisecond and
        // "ExpiresAtUtc <= now" is equivalent to "ExpiresAtUtc <= now truncated to the
        // millisecond". Truncating here keeps that boundary exact instead of depending on
        // how SqlClient rounds a sub-millisecond parameter for a datetime2(3) column.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

        var cached = await TryGetCachedAsync(code);
        // A hit past its expiration (clock skew between app and Redis, or an expiry change whose
        // invalidation failed) is ignored; SQL Server decides and the fill below overwrites it.
        if (cached is not null && (cached.ExpiresAtUtc is null || cached.ExpiresAtUtc > now))
        {
            return new ResolvedShortCode(cached.UrlMappingId, cached.OriginalUrl);
        }

        // The ShortCode column's binary collation makes the SQL comparison case-sensitive.
        var match = await _dbContext.UrlMappings
            .AsNoTracking()
            .Where(m => m.ShortCode == code
                && m.IsActive
                && (m.ExpiresAtUtc == null || m.ExpiresAtUtc > now))
            .Select(m => new { m.Id, m.ShortCode, m.OriginalUrl, m.ExpiresAtUtc })
            .FirstOrDefaultAsync(cancellationToken);

        // Re-checked ordinally so a collation change could never redirect "abc1234" to the
        // mapping for "ABC1234" (or cache it under the wrong key).
        if (match is null || !string.Equals(match.ShortCode, code, StringComparison.Ordinal))
        {
            return null;
        }

        if (RedirectCacheOptions.GetTimeToLive(match.ExpiresAtUtc, now, _maxTimeToLive) is { } timeToLive)
        {
            await TrySetCachedAsync(code, new CachedRedirect(match.Id, match.OriginalUrl, match.ExpiresAtUtc), timeToLive);
        }

        return new ResolvedShortCode(match.Id, match.OriginalUrl);
    }

    private async Task<CachedRedirect?> TryGetCachedAsync(string code)
    {
        try
        {
            return await _cache.GetAsync(code);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redirect cache lookup failed for {ShortCode}; using SQL Server.", code);
            return null;
        }
    }

    private async Task TrySetCachedAsync(string code, CachedRedirect entry, TimeSpan timeToLive)
    {
        try
        {
            await _cache.SetAsync(code, entry, timeToLive);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redirect cache fill failed for {ShortCode}; the redirect is unaffected.", code);
        }
    }
}
