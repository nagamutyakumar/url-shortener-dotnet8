using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.ShortCodes;

namespace UrlShortener.Api.Analytics;

/// <summary>Aggregates persisted click events for one short code.</summary>
/// <remarks>
/// Counts only events the writer has persisted, so the most recent clicks may not be included
/// yet, and events lost to queue overflow or write failures never are. The summary and the
/// country breakdown are two queries without a shared snapshot, so a click persisted between
/// them can make the per-country numbers differ slightly from the totals.
/// </remarks>
public sealed class ClickAnalyticsService
{
    private readonly UrlShortenerDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public ClickAnalyticsService(UrlShortenerDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    /// <returns>Null when the code is malformed or no mapping has exactly this code.</returns>
    public async Task<ClickAnalyticsResponse?> GetAsync(string code, CancellationToken cancellationToken)
    {
        if (!ShortCodeFormat.IsValid(code))
        {
            return null;
        }

        // Unlike redirects, inactive and expired mappings are included.
        var mapping = await _dbContext.UrlMappings
            .AsNoTracking()
            .Where(m => m.ShortCode == code)
            .Select(m => new { m.Id, m.ShortCode, m.IsActive, m.ExpiresAtUtc })
            .FirstOrDefaultAsync(cancellationToken);

        // Same ordinal re-check as ShortCodeResolver, in case the column collation changes.
        if (mapping is null || !string.Equals(mapping.ShortCode, code, StringComparison.Ordinal))
        {
            return null;
        }

        // Null when the mapping has no clicks: the GROUP BY then yields no row.
        var summary = await SummaryQuery(_dbContext, mapping.Id).FirstOrDefaultAsync(cancellationToken)
            ?? new ClickSummary(0, 0, 0, 0);
        var countries = await CountryQuery(_dbContext, mapping.Id).ToListAsync(cancellationToken);

        return new ClickAnalyticsResponse(
            mapping.ShortCode,
            GetLinkStatus(mapping.IsActive, mapping.ExpiresAtUtc),
            mapping.ExpiresAtUtc,
            summary.TotalClicks,
            ToUniqueVisitors(summary),
            countries,
            summary.ClicksWithUnknownCountry);
    }

    /// <summary>
    /// One row of COUNT_BIG aggregates over the mapping's clicks (no rows when there are none).
    /// The UrlMappingId filter seeks IX_ClickEvents_UrlMappingId_ClickedAtUtc.
    /// </summary>
    public static IQueryable<ClickSummary> SummaryQuery(UrlShortenerDbContext dbContext, long urlMappingId) =>
        dbContext.ClickEvents
            .Where(e => e.UrlMappingId == urlMappingId)
            .GroupBy(e => e.UrlMappingId)
            .Select(g => new ClickSummary(
                g.LongCount(),
                g.LongCount(e => e.VisitorHash != null),
                g.Where(e => e.VisitorHash != null).Select(e => e.VisitorHash).Distinct().LongCount(),
                g.LongCount(e => e.CountryCode == null)));

    public static IQueryable<CountryClicks> CountryQuery(UrlShortenerDbContext dbContext, long urlMappingId) =>
        dbContext.ClickEvents
            .Where(e => e.UrlMappingId == urlMappingId && e.CountryCode != null)
            .GroupBy(e => e.CountryCode!)
            .OrderByDescending(g => g.LongCount())
            .ThenBy(g => g.Key)
            .Select(g => new CountryClicks(g.Key, g.LongCount()));

    private string GetLinkStatus(bool isActive, DateTime? expiresAtUtc)
    {
        if (!isActive)
        {
            return LinkStatuses.Inactive;
        }

        // Matches ShortCodeResolver's "ExpiresAtUtc <= now" rule. No millisecond truncation is
        // needed here: the comparison runs in memory, not as a SQL parameter.
        return expiresAtUtc is { } expires && expires <= _timeProvider.GetUtcNow().UtcDateTime
            ? LinkStatuses.Expired
            : LinkStatuses.Active;
    }

    public static UniqueVisitors ToUniqueVisitors(ClickSummary summary)
    {
        if (summary.ClicksWithVisitorHash == summary.TotalClicks)
        {
            return new UniqueVisitors(
                UniqueVisitorStatuses.Complete, summary.UniqueVisitorHashes, summary.ClicksWithVisitorHash);
        }

        return summary.ClicksWithVisitorHash == 0
            ? new UniqueVisitors(UniqueVisitorStatuses.Unavailable, null, 0)
            : new UniqueVisitors(
                UniqueVisitorStatuses.Partial, summary.UniqueVisitorHashes, summary.ClicksWithVisitorHash);
    }
}

public sealed record ClickSummary(
    long TotalClicks,
    long ClicksWithVisitorHash,
    long UniqueVisitorHashes,
    long ClicksWithUnknownCountry);
