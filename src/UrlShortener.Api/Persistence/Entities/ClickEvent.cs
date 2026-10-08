namespace UrlShortener.Api.Persistence.Entities;

public sealed class ClickEvent
{
    public long Id { get; set; }

    public long UrlMappingId { get; set; }

    public DateTime ClickedAtUtc { get; set; }

    /// <summary>
    /// Keyed hash of request attributes: an approximate, privacy-conscious signal,
    /// not a reliable visitor identity. Null when it cannot be computed.
    /// </summary>
    public byte[]? VisitorHash { get; set; }

    /// <summary>ISO 3166-1 alpha-2 code, only when supplied by a trusted upstream.</summary>
    public string? CountryCode { get; set; }

    public string? Browser { get; set; }

    public UrlMapping UrlMapping { get; set; } = null!;
}
