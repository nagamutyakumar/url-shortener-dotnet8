using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.ShortCodes;

namespace UrlShortener.Api.Redirection;

public sealed record ResolvedShortCode(long UrlMappingId, string OriginalUrl);

/// <summary>
/// Resolves a short code to the URL it redirects to.
/// </summary>
/// <remarks>
/// A code resolves only when it is well formed, matches a stored code exactly (case-sensitive),
/// and the mapping is active and not expired. Expired means <c>ExpiresAtUtc &lt;= now</c>.
/// The destination URL is never contacted.
/// </remarks>
public sealed class ShortCodeResolver
{
    private readonly UrlShortenerDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public ShortCodeResolver(UrlShortenerDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    /// <returns>The mapping id and original URL, or null when the code does not resolve.</returns>
    public async Task<ResolvedShortCode?> ResolveAsync(string code, CancellationToken cancellationToken)
    {
        // Malformed codes can never match, so they are rejected without a query.
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

        // The ShortCode column's binary collation makes the SQL comparison case-sensitive.
        var match = await _dbContext.UrlMappings
            .AsNoTracking()
            .Where(m => m.ShortCode == code
                && m.IsActive
                && (m.ExpiresAtUtc == null || m.ExpiresAtUtc > now))
            .Select(m => new { m.Id, m.ShortCode, m.OriginalUrl })
            .FirstOrDefaultAsync(cancellationToken);

        // Re-checked ordinally so a collation change could never redirect "abc1234" to the
        // mapping for "ABC1234".
        return match is not null && string.Equals(match.ShortCode, code, StringComparison.Ordinal)
            ? new ResolvedShortCode(match.Id, match.OriginalUrl)
            : null;
    }
}
