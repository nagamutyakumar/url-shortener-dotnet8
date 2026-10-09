using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace UrlShortener.Api.Health;

/// <summary>
/// Readiness check for the optional redirect cache. Registered only when Redis is configured, with
/// a failure status of Degraded: redirects fall back to SQL Server, so a Redis outage should not
/// take the instance out of a load balancer.
/// </summary>
public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _multiplexer;

    public RedisHealthCheck(IConnectionMultiplexer multiplexer)
    {
        _multiplexer = multiplexer;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // The multiplexer reconnects in the background; while it is down, report that without
        // issuing a command (FailFast would reject it anyway).
        if (!_multiplexer.IsConnected)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Not connected.");
        }

        // PingAsync takes no token; WaitAsync lets the registration timeout end the wait. Errors
        // are mapped to the failure status by the health check service.
        await _multiplexer.GetDatabase().PingAsync().WaitAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
