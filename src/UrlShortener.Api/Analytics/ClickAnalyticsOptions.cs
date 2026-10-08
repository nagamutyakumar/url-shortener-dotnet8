namespace UrlShortener.Api.Analytics;

public sealed class ClickAnalyticsOptions
{
    public const string SectionName = "Analytics";

    public const int MinVisitorHashSecretLength = 32;

    /// <summary>
    /// Maximum number of click events buffered in memory. When full, new events are dropped
    /// (and counted) rather than delaying redirects.
    /// </summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>Maximum number of click events inserted per SaveChanges call.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// HMAC key for <see cref="Persistence.Entities.ClickEvent.VisitorHash"/>; at least
    /// <see cref="MinVisitorHashSecretLength"/> characters. Supply via user-secrets or the
    /// Analytics__VisitorHashSecret environment variable, never appsettings. When unset,
    /// VisitorHash is stored as null and unique visitors are reported as unavailable.
    /// Rotating it makes returning visitors look new.
    /// </summary>
    public string? VisitorHashSecret { get; set; }

    public static bool IsValid(ClickAnalyticsOptions options) =>
        options.QueueCapacity is >= 1 and <= 1_000_000
        && options.BatchSize is >= 1 and <= 1_000
        && (string.IsNullOrEmpty(options.VisitorHashSecret)
            || options.VisitorHashSecret.Length >= MinVisitorHashSecretLength);
}
