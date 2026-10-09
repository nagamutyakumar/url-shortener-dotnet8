namespace UrlShortener.Api.RateLimiting;

/// <summary>
/// Fixed-window limits per client IP, bound from the <c>RateLimiting</c> section. Each policy
/// counts separately: a client can use its full mutation and analytics budgets in the same window.
/// </summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Shared by POST, PUT and DELETE on <c>/api/v1/shorten</c>.</summary>
    public FixedWindowPolicyOptions Mutations { get; set; } = new() { PermitLimit = 20 };

    /// <summary>GET <c>/api/v1/analytics/{code}</c>.</summary>
    public FixedWindowPolicyOptions Analytics { get; set; } = new() { PermitLimit = 60 };

    public static bool IsValid(RateLimitingOptions options) =>
        FixedWindowPolicyOptions.IsValid(options.Mutations)
        && FixedWindowPolicyOptions.IsValid(options.Analytics);
}

public sealed class FixedWindowPolicyOptions
{
    public const int MaxPermitLimit = 100_000;
    public const int MaxQueueLimit = 1_000;
    public static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(1);

    /// <summary>Requests allowed per client per window.</summary>
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Requests held open waiting for the next window once the limit is reached. 0 rejects them
    /// at once, so a client over its limit cannot tie up server connections.
    /// </summary>
    public int QueueLimit { get; set; }

    public static bool IsValid(FixedWindowPolicyOptions? options) =>
        options is not null
        && options.PermitLimit is >= 1 and <= MaxPermitLimit
        && options.Window >= MinWindow && options.Window <= MaxWindow
        && options.QueueLimit is >= 0 and <= MaxQueueLimit;
}
