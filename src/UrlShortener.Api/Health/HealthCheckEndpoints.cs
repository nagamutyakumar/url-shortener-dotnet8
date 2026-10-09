using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using UrlShortener.Api.Caching;

namespace UrlShortener.Api.Health;

/// <summary>
/// Liveness and readiness endpoints on the built-in ASP.NET Core health checks (part of the .NET 8
/// shared framework, so no new package). See docs/health-checks.md.
/// </summary>
public static class HealthCheckEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    public const string SqlServerCheckName = "sql";
    public const string RedisCheckName = "redis";
    public const string ReadyTag = "ready";

    /// <summary>The application's SQL Server connection string, as read by Program.</summary>
    public const string SqlServerConnectionStringName = "UrlShortener";

    /// <summary>Checks run in parallel, so a readiness request takes at most the longest of these.</summary>
    public static readonly TimeSpan SqlServerTimeout = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan RedisTimeout = TimeSpan.FromSeconds(2);

    public static IServiceCollection AddApiHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        // The probe gets its own connection string (see SqlServerHealthCheck), not the DbContext,
        // so its short timeouts never apply to application queries.
        var checks = services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                SqlServerCheckName,
                _ => new SqlServerHealthCheck(configuration.GetConnectionString(SqlServerConnectionStringName)
                    ?? throw new InvalidOperationException(
                        $"Connection string '{SqlServerConnectionStringName}' is not configured.")),
                HealthStatus.Unhealthy,
                [ReadyTag],
                SqlServerTimeout));

        // Same switch as AddRedirectCache: without Redis there is nothing to check or report.
        if (RedirectCacheServiceCollectionExtensions.IsRedisConfigured(configuration))
        {
            checks.AddCheck<RedisHealthCheck>(
                RedisCheckName, HealthStatus.Degraded, [ReadyTag], RedisTimeout);
        }

        return services;
    }

    /// <summary>
    /// Liveness runs no checks, so it answers 200 whenever the process can serve requests.
    /// Readiness runs the "ready" checks: 200 for Healthy and Degraded, 503 for Unhealthy (the
    /// middleware defaults). Neither endpoint is rate limited.
    /// </summary>
    public static IEndpointRouteBuilder MapApiHealthChecks(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks(LivePath, new HealthCheckOptions
            {
                Predicate = _ => false,
                ResponseWriter = WriteResponseAsync,
            })
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get, HttpMethods.Head]))
            .DisableRateLimiting();

        app.MapHealthChecks(ReadyPath, new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains(ReadyTag),
                ResponseWriter = WriteResponseAsync,
            })
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get, HttpMethods.Head]))
            .DisableRateLimiting();

        return app;
    }

    /// <summary>
    /// Statuses and durations only. Descriptions and exceptions are left out because the health
    /// check service fills them from exception messages, which can name hosts, endpoints or
    /// credentials; failures are logged server-side instead.
    /// </summary>
    public static Task WriteResponseAsync(HttpContext httpContext, HealthReport report)
    {
        var response = new HealthResponse(
            report.Status.ToString(),
            ToMilliseconds(report.TotalDuration),
            report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new HealthCheckResponse(entry.Value.Status.ToString(), ToMilliseconds(entry.Value.Duration))));

        return httpContext.Response.WriteAsJsonAsync(response, httpContext.RequestAborted);
    }

    private static double ToMilliseconds(TimeSpan duration) => Math.Round(duration.TotalMilliseconds, 1);
}

public sealed record HealthResponse(string Status, double TotalDurationMs, IReadOnlyDictionary<string, HealthCheckResponse> Checks);

public sealed record HealthCheckResponse(string Status, double DurationMs);
