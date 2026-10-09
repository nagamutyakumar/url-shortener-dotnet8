using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using UrlShortener.Api.Health;

namespace UrlShortener.UnitTests.Health;

// The readiness SQL probe on its own: its connection string, and that both the check's token and
// SqlClient's own timeouts bound it. No SQL Server is needed.
public sealed class SqlServerHealthCheckTests
{
    private const string AppConnectionString =
        "Server=tcp:sql.example,1433;Database=UrlShortener;User Id=app;Password=Secret-1;TrustServerCertificate=True";

    [Fact]
    public void CreateProbeConnectionString_BoundsTimeoutsAndDisablesPooling_KeepingTheTarget()
    {
        var probe = new SqlConnectionStringBuilder(SqlServerHealthCheck.CreateProbeConnectionString(AppConnectionString));

        Assert.Equal(SqlServerHealthCheck.ConnectTimeoutSeconds, probe.ConnectTimeout);
        Assert.Equal(SqlServerHealthCheck.CommandTimeoutSeconds, probe.CommandTimeout);
        Assert.Equal(0, probe.ConnectRetryCount);
        Assert.False(probe.Pooling);
        Assert.Equal("tcp:sql.example,1433", probe.DataSource);
        Assert.Equal("UrlShortener", probe.InitialCatalog);
        Assert.Equal("app", probe.UserID);
        Assert.Equal("Secret-1", probe.Password);
        Assert.True(probe.TrustServerCertificate);
    }

    [Fact]
    public void CreateProbeConnectionString_OverridesLongerTimeoutsFromTheAppConnectionString()
    {
        var probe = new SqlConnectionStringBuilder(SqlServerHealthCheck.CreateProbeConnectionString(
            AppConnectionString + ";Connect Timeout=60;Command Timeout=120;Pooling=true"));

        Assert.Equal(SqlServerHealthCheck.ConnectTimeoutSeconds, probe.ConnectTimeout);
        Assert.Equal(SqlServerHealthCheck.CommandTimeoutSeconds, probe.CommandTimeout);
        Assert.False(probe.Pooling);
    }

    [Fact]
    public void ProbeCommands_InheritTheCommandTimeout()
    {
        using var connection = new SqlConnection(SqlServerHealthCheck.CreateProbeConnectionString(AppConnectionString));
        using var command = connection.CreateCommand();

        Assert.Equal(SqlServerHealthCheck.CommandTimeoutSeconds, command.CommandTimeout);
    }

    [Fact]
    public async Task CheckHealthAsync_ProbeSucceeds_IsHealthy()
    {
        var check = new SqlServerHealthCheck(_ => Task.CompletedTask);

        var result = await check.CheckHealthAsync(Context(check));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_ProbeIgnoresTheToken_StopsWaitingWhenTheTokenIsCancelled()
    {
        var neverCompletes = new TaskCompletionSource();
        var check = new SqlServerHealthCheck(_ => neverCompletes.Task);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var checking = check.CheckHealthAsync(Context(check), timeout.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(neverCompletes.Task.IsCompleted);
    }

    [Fact]
    public async Task CheckHealthAsync_ServerAcceptsButNeverAnswers_FailsWithinTheConnectTimeoutWithoutAToken()
    {
        // Like a container port proxy whose SQL Server is stopped: TCP connects, the login never
        // completes. With no token at all, only the probe's Connect Timeout can end this.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = new ConcurrentBag<Socket>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    accepted.Add(await listener.AcceptSocketAsync());
                }
            }
            catch (Exception)
            {
                // The listener was stopped at the end of the test.
            }
        });
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var check = new SqlServerHealthCheck(
            $"Server=tcp:127.0.0.1,{port};Database=x;User Id=x;Password=x;TrustServerCertificate=True");

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<SqlException>(() => check.CheckHealthAsync(Context(check), CancellationToken.None));
        stopwatch.Stop();

        // SqlClient's default Connect Timeout is 15 s; generous upper bound for slow CI machines.
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(SqlServerHealthCheck.ConnectTimeoutSeconds + 5));
        listener.Stop();
        foreach (var socket in accepted)
        {
            socket.Dispose();
        }
    }

    private static HealthCheckContext Context(IHealthCheck check) => new()
    {
        Registration = new HealthCheckRegistration(
            HealthCheckEndpoints.SqlServerCheckName, check, HealthStatus.Unhealthy, [HealthCheckEndpoints.ReadyTag]),
    };
}
