namespace UrlShortener.Api.Shortening;

/// <summary>Expiration rules shared by creating (POST) and updating (PUT) a short URL.</summary>
public static class ExpirationRules
{
    /// <summary>
    /// Converts to UTC and requires a future instant. Truncates to the millisecond
    /// precision of the datetime2(3) column so responses and comparisons match what is
    /// stored. The explicit-offset requirement is enforced earlier, during JSON binding,
    /// by <see cref="ExplicitOffsetDateTimeOffsetConverter"/>. Null means "no expiration".
    /// </summary>
    public static bool TryNormalize(
        DateTimeOffset? requested,
        DateTime nowUtc,
        out DateTime? expiresAtUtc,
        out string error)
    {
        expiresAtUtc = null;
        error = string.Empty;

        if (requested is not { } value)
        {
            return true;
        }

        var utc = value.UtcDateTime;
        utc = new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

        if (utc <= nowUtc)
        {
            error = "The expiration must be in the future.";
            return false;
        }

        expiresAtUtc = utc;
        return true;
    }
}
