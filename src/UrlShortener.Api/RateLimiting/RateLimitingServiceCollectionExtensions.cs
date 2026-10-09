using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace UrlShortener.Api.RateLimiting;

/// <summary>
/// Per-client-IP fixed-window rate limiting with the built-in ASP.NET Core middleware. Limits are
/// in-memory, so each app instance counts separately (see docs/rate-limiting.md).
/// </summary>
public static class RateLimitingServiceCollectionExtensions
{
    public const string MutationsPolicy = "mutations";
    public const string AnalyticsPolicy = "analytics";

    /// <summary>
    /// Partition for requests without a remote IP, e.g. in-process test hosts. All such requests
    /// share one budget, so this fails towards limiting rather than exempting them.
    /// </summary>
    public const string UnknownClientPartition = "unknown";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .Validate(
                RateLimitingOptions.IsValid,
                $"{RateLimitingOptions.SectionName}: each policy's PermitLimit must be 1-{FixedWindowPolicyOptions.MaxPermitLimit}, " +
                $"Window {FixedWindowPolicyOptions.MinWindow}-{FixedWindowPolicyOptions.MaxWindow}, " +
                $"and QueueLimit 0-{FixedWindowPolicyOptions.MaxQueueLimit}.")
            .ValidateOnStart();

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<RateLimitingOptions>>((limiter, settings) =>
            {
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.OnRejected = WriteRejectionAsync;
                AddFixedWindowPolicy(limiter, MutationsPolicy, settings.Value.Mutations);
                AddFixedWindowPolicy(limiter, AnalyticsPolicy, settings.Value.Analytics);
            });

        return services;
    }

    /// <summary>
    /// The direct peer's address. X-Forwarded-For is deliberately not read: any client can set it,
    /// so trusting it without a known proxy would let callers pick their own partition.
    /// IPv4-mapped IPv6 (::ffff:1.2.3.4, seen on dual-stack sockets) is reduced to IPv4 so one
    /// client never gets two budgets.
    /// </summary>
    public static string GetPartitionKey(IPAddress? remoteIpAddress)
    {
        if (remoteIpAddress is null)
        {
            return UnknownClientPartition;
        }

        return remoteIpAddress.IsIPv4MappedToIPv6
            ? remoteIpAddress.MapToIPv4().ToString()
            : remoteIpAddress.ToString();
    }

    private static void AddFixedWindowPolicy(RateLimiterOptions limiter, string policyName, FixedWindowPolicyOptions settings) =>
        limiter.AddPolicy(policyName, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            GetPartitionKey(httpContext.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.PermitLimit,
                Window = settings.Window,
                QueueLimit = settings.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            }));

    /// <summary>
    /// 429 as problem details, in the same format as the API's other errors. Retry-After is whole
    /// seconds, rounded up, and only sent when the limiter reports it. The body names neither the
    /// policy nor the limit.
    /// </summary>
    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        var problemDetailsService = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                // ASP.NET Core has no default type for 429, unlike the API's other error codes.
                Type = "https://tools.ietf.org/html/rfc6585#section-4",
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests.",
                Detail = "The rate limit for this client was exceeded. Retry the request later.",
            },
        });
    }
}
