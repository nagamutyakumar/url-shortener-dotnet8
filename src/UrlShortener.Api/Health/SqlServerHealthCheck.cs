using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace UrlShortener.Api.Health;

/// <summary>
/// Readiness check for SQL Server: opens its own short-lived connection and runs <c>SELECT 1</c>.
/// </summary>
/// <remarks>
/// The registration timeout only cancels the check's token; the health check service still awaits
/// the check. A SqlClient open that does not observe the token runs until the connection string's
/// Connect Timeout (15 s by default), which is how a SQL outage made readiness take ~15 s. So the
/// probe bounds itself twice, without touching the app's own DbContext connection:
/// <list type="bullet">
/// <item>its own connection string with short connect and command timeouts and no pooling, so the
/// abandoned work ends by itself and never shares pool state with application requests;</item>
/// <item><c>WaitAsync</c> on the check's token, so the registration timeout ends the wait even when
/// SqlClient does not.</item>
/// </list>
/// Errors propagate and the health check service maps them to the failure status.
/// </remarks>
public sealed class SqlServerHealthCheck : IHealthCheck
{
    public const int ConnectTimeoutSeconds = 2;
    public const int CommandTimeoutSeconds = 2;

    private readonly Func<CancellationToken, Task> _probe;

    public SqlServerHealthCheck(string connectionString)
    {
        var probeConnectionString = CreateProbeConnectionString(connectionString);
        _probe = cancellationToken => ProbeAsync(probeConnectionString, cancellationToken);
    }

    /// <summary>For tests: runs <paramref name="probe"/> in place of the SQL Server round trip.</summary>
    public SqlServerHealthCheck(Func<CancellationToken, Task> probe)
    {
        _probe = probe;
    }

    /// <summary>
    /// Same server, database and credentials as the application, with bounded timeouts. Pooling is
    /// off so each probe tests a fresh login and a failed probe leaves no pool in a blocking period;
    /// ConnectRetryCount 0 stops idle-connection resiliency from adding a reconnect delay.
    /// </summary>
    public static string CreateProbeConnectionString(string connectionString) =>
        new SqlConnectionStringBuilder(connectionString)
        {
            ConnectTimeout = ConnectTimeoutSeconds,
            CommandTimeout = CommandTimeoutSeconds,
            ConnectRetryCount = 0,
            Pooling = false,
        }.ConnectionString;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // If the token fires first, the probe is abandoned and finishes on its own within the
        // SqlClient timeouts above.
        await _probe(cancellationToken).WaitAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }

    private static async Task ProbeAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
