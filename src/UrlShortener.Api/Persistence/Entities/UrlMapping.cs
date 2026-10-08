namespace UrlShortener.Api.Persistence.Entities;

public sealed class UrlMapping
{
    public long Id { get; set; }

    public string ShortCode { get; set; } = null!;

    public string OriginalUrl { get; set; } = null!;

    /// <summary>
    /// SHA-256 of the normalized original URL, used to find duplicates without
    /// indexing the full URL. Not unique: always confirm by comparing OriginalUrl.
    /// </summary>
    public byte[] OriginalUrlHash { get; set; } = null!;

    /// <summary>Set by <see cref="UrlShortenerDbContext"/> on insert.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Set by <see cref="UrlShortenerDbContext"/> on insert and update.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public bool IsActive { get; set; } = true;

    public byte[] RowVersion { get; set; } = null!;

    public List<ClickEvent> ClickEvents { get; } = new();
}
