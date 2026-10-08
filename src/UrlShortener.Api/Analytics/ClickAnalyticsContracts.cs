namespace UrlShortener.Api.Analytics;

/// <param name="LinkStatus">One of <see cref="LinkStatuses"/>. Analytics stay readable after a
/// link is deactivated or expires, because click history is retained.</param>
/// <param name="Countries">Clicks per stored ISO 3166-1 alpha-2 code, most clicks first. Only
/// codes supplied by a trusted source are listed; none are inferred.</param>
/// <param name="ClicksWithUnknownCountry">Clicks with no country recorded. Equals
/// <paramref name="TotalClicks"/> while no geolocation source is configured.</param>
public sealed record ClickAnalyticsResponse(
    string ShortCode,
    string LinkStatus,
    DateTime? ExpiresAtUtc,
    long TotalClicks,
    UniqueVisitors UniqueVisitors,
    IReadOnlyList<CountryClicks> Countries,
    long ClicksWithUnknownCountry);

/// <param name="Status">One of <see cref="UniqueVisitorStatuses"/>.</param>
/// <param name="Count">Distinct visitor hashes among the clicks that have one; null when
/// <paramref name="Status"/> is "unavailable". An approximation even when "complete": see
/// <see cref="VisitorHasher"/>.</param>
/// <param name="ClicksWithVisitorHash">How many of the clicks <paramref name="Count"/> is based on.</param>
public sealed record UniqueVisitors(string Status, long? Count, long ClicksWithVisitorHash);

public sealed record CountryClicks(string CountryCode, long Clicks);

public static class LinkStatuses
{
    public const string Active = "active";
    public const string Inactive = "inactive";
    public const string Expired = "expired";
}

public static class UniqueVisitorStatuses
{
    /// <summary>Every click has a visitor hash (including zero clicks): Count is meaningful.</summary>
    public const string Complete = "complete";

    /// <summary>Only some clicks have a visitor hash: Count covers only those clicks.</summary>
    public const string Partial = "partial";

    /// <summary>There are clicks but none has a visitor hash: Count is null, not zero.</summary>
    public const string Unavailable = "unavailable";
}
