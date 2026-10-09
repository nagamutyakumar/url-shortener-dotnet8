namespace UrlShortener.Api.Caching;

/// <summary>
/// Redirect cache settings. The Redis connection itself is <c>ConnectionStrings:Redis</c>, set via
/// user-secrets or the <c>ConnectionStrings__Redis</c> environment variable; when it is unset the
/// cache is disabled and every redirect reads SQL Server.
/// </summary>
public sealed class RedirectCacheOptions
{
    public const string SectionName = "RedirectCache";

    public static readonly TimeSpan MinTimeToLive = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxAllowedTimeToLive = TimeSpan.FromDays(1);

    /// <summary>
    /// Upper bound on how long an entry lives, and so on how long a redirect can stay stale after
    /// an invalidation fails or races with a cache fill. Entries never outlive the link's own
    /// expiration.
    /// </summary>
    public TimeSpan MaxTimeToLive { get; set; } = TimeSpan.FromMinutes(5);

    public static bool IsValid(RedirectCacheOptions options) =>
        options.MaxTimeToLive >= MinTimeToLive && options.MaxTimeToLive <= MaxAllowedTimeToLive;

    /// <summary>
    /// <c>min(maxTimeToLive, expiresAtUtc - nowUtc)</c>, or null when the entry should not be cached
    /// because less than a millisecond of the link's lifetime is left.
    /// </summary>
    public static TimeSpan? GetTimeToLive(DateTime? expiresAtUtc, DateTime nowUtc, TimeSpan maxTimeToLive)
    {
        if (expiresAtUtc is not { } expires)
        {
            return maxTimeToLive;
        }

        var remaining = expires - nowUtc;
        if (remaining < TimeSpan.FromMilliseconds(1))
        {
            return null;
        }

        return remaining < maxTimeToLive ? remaining : maxTimeToLive;
    }
}
