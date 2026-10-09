namespace UrlShortener.Api.Caching;

/// <summary>What a redirect needs, cached per short code. Never holds inactive or expired links.</summary>
/// <param name="UrlMappingId">Needed to queue the click event on a cache hit.</param>
/// <param name="OriginalUrl">Canonical destination URL.</param>
/// <param name="ExpiresAtUtc">Re-checked on every hit, so a stale entry cannot outlive the link.</param>
public sealed record CachedRedirect(long UrlMappingId, string OriginalUrl, DateTime? ExpiresAtUtc);

/// <summary>
/// Cache of successful short-code resolutions. SQL Server stays authoritative; this only saves
/// the lookup for hot codes.
/// </summary>
/// <remarks>
/// Implementations may throw (for example when Redis is unreachable). Callers decide how to
/// degrade: lookups fall back to SQL Server, and a failed removal is logged without failing the
/// committed write. There is no CancellationToken because StackExchange.Redis does not take one;
/// its own timeouts bound each call.
/// </remarks>
public interface IRedirectCache
{
    /// <returns>The cached entry, or null on a miss.</returns>
    Task<CachedRedirect?> GetAsync(string shortCode);

    Task SetAsync(string shortCode, CachedRedirect entry, TimeSpan timeToLive);

    /// <summary>Removes the entry, if any. Throws when the removal could not be performed.</summary>
    Task RemoveAsync(string shortCode);
}

/// <summary>Used when no Redis connection string is configured: every lookup is a miss.</summary>
public sealed class NullRedirectCache : IRedirectCache
{
    public static readonly NullRedirectCache Instance = new();

    public Task<CachedRedirect?> GetAsync(string shortCode) => Task.FromResult<CachedRedirect?>(null);

    public Task SetAsync(string shortCode, CachedRedirect entry, TimeSpan timeToLive) => Task.CompletedTask;

    public Task RemoveAsync(string shortCode) => Task.CompletedTask;
}
