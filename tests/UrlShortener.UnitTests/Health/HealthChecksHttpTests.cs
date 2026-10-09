using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Health;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Health;

// Runs the real Program pipeline in-process. The "sql" check opens its own SqlConnection, so every
// factory swaps that registration's check for a test double (healthy unless a test replaces it);
// SqlServerHealthCheckTests covers the real probe. Redis, when configured, is a real
// StackExchange.Redis client pointed at a port where nothing listens. No test waits on the clock:
// the timeout tests shorten the registration's timeout and use checks that never finish on their own.
public sealed class HealthChecksHttpTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private const string UnreachableRedis = "127.0.0.1:1,password=RedisSecret-123,connectTimeout=250,connectRetry=0";

    [Fact]
    public async Task Live_Returns200Healthy_WithoutRunningAnyCheck()
    {
        var sql = new FakeHealthCheck(_ => throw new InvalidOperationException("must not run"));
        var redis = new FakeHealthCheck(_ => throw new InvalidOperationException("must not run"));
        await using var factory = CreateFactory(redis: UnreachableRedis, configure: services =>
        {
            ReplaceCheck(services, HealthCheckEndpoints.SqlServerCheckName, sql);
            ReplaceCheck(services, HealthCheckEndpoints.RedisCheckName, redis);
        });

        using var response = await factory.CreateClient().GetAsync(HealthCheckEndpoints.LivePath);
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
        Assert.Empty(body.RootElement.GetProperty("checks").EnumerateObject());
        Assert.Equal(0, sql.Calls);
        Assert.Equal(0, redis.Calls);
    }

    [Fact]
    public async Task Ready_SqlHealthyAndRedisNotConfigured_Returns200HealthyWithOnlySql()
    {
        await using var factory = CreateFactory();

        using var response = await factory.CreateClient().GetAsync(HealthCheckEndpoints.ReadyPath);
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
        var check = Assert.Single(body.RootElement.GetProperty("checks").EnumerateObject());
        Assert.Equal("sql", check.Name);
        Assert.Equal("Healthy", check.Value.GetProperty("status").GetString());
        Assert.True(check.Value.GetProperty("durationMs").GetDouble() >= 0);
        Assert.True(body.RootElement.GetProperty("totalDurationMs").GetDouble() >= 0);
    }

    [Fact]
    public async Task Ready_SqlUnavailable_Returns503Unhealthy_WithoutLeakingExceptionDetails()
    {
        // The health check service copies an exception's message into the entry's description.
        var sql = new FakeHealthCheck(_ => throw new InvalidOperationException(
            "Login failed. Server=sql-secret.internal;User Id=sa;Password=SqlSecret-456"));
        await using var factory = CreateFactory(configure: services =>
            ReplaceCheck(services, HealthCheckEndpoints.SqlServerCheckName, sql));

        using var response = await factory.CreateClient().GetAsync(HealthCheckEndpoints.ReadyPath);
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Unhealthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Unhealthy", body.RootElement.GetProperty("checks").GetProperty("sql").GetProperty("status").GetString());
        Assert.Equal(1, sql.Calls);
        AssertNoSensitiveDetails(json);
    }

    [Fact]
    public async Task Ready_SqlReportsCannotConnect_Returns503()
    {
        var sql = new FakeHealthCheck(context =>
            new HealthCheckResult(context.Registration.FailureStatus, "Cannot connect to the database."));
        await using var factory = CreateFactory(configure: services =>
            ReplaceCheck(services, HealthCheckEndpoints.SqlServerCheckName, sql));

        using var response = await factory.CreateClient().GetAsync(HealthCheckEndpoints.ReadyPath);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("Cannot connect", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ready_SqlCheckExceedsTimeout_IsCancelledAndReturns503()
    {
        var sql = new FakeHealthCheck(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return HealthCheckResult.Healthy();
        });
        await using var factory = CreateFactory(configure: services =>
            ReplaceCheck(services, HealthCheckEndpoints.SqlServerCheckName, sql, timeout: TimeSpan.FromMilliseconds(50)));

        using var response = await factory.CreateClient().GetAsync(HealthCheckEndpoints.ReadyPath);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(sql.Cancelled);
        AssertNoSensitiveDetails(json);
    }

    [Fact]
    public async Task Ready_SqlProbeIgnoresCancellation_TimeoutStillReturns503()
    {
        // A SqlClient open that does not observe the token: the registration timeout alone would
        // wait for it (the original ~15 s outage response), so SqlServerHealthCheck must end the wait.
        var neverCompletes = new TaskCompletionSource();
        var sql = new SqlServerHealthCheck(_ => neverCompletes.Task);
        await using var factory = CreateFactory(configure: services =>
            ReplaceCheck(services, HealthCheckEndpoints.SqlServerCheckName, sql, timeout: TimeSpan.FromMilliseconds(50)));

        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30); // fails instead of hanging if the wait is unbounded

        using var response = await client.GetAsync(HealthCheckEndpoints.ReadyPath);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertNoSensitiveDetails(json);
    }

    [Fact]
    public async Task Ready_RedisConfiguredButUnreachable_SqlHealthy_Returns200Degraded()
    {
        await using var factory = CreateFactory(redis: UnreachableRedis);

        using var response = await factory.CreateClient().GetAsync(HealthCheckEndpoints.ReadyPath);
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", body.RootElement.GetProperty("status").GetString());
        var checks = body.RootElement.GetProperty("checks");
        Assert.Equal("Healthy", checks.GetProperty("sql").GetProperty("status").GetString());
        Assert.Equal("Degraded", checks.GetProperty("redis").GetProperty("status").GetString());
        AssertNoSensitiveDetails(json);
        Assert.DoesNotContain("127.0.0.1", json);
    }

    [Fact]
    public async Task Ready_RedisPingThrows_IsDegradedWithoutDetails()
    {
        var redis = new FakeHealthCheck(_ => throw new InvalidOperationException(
            "Timeout performing PING on redis-secret.internal:6379, password=RedisSecret-123"));
        await using var factory = CreateFactory(redis: UnreachableRedis, configure: services =>
            ReplaceCheck(services, HealthCheckEndpoints.RedisCheckName, redis));

        using var response = await factory.CreateClient().GetAsync(HealthCheckEndpoints.ReadyPath);
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", body.RootElement.GetProperty("status").GetString());
        AssertNoSensitiveDetails(json);
    }

    [Fact]
    public async Task Ready_SqlUnavailableAndRedisUnreachable_Returns503Unhealthy()
    {
        var sql = new FakeHealthCheck(context => new HealthCheckResult(context.Registration.FailureStatus));
        await using var factory = CreateFactory(redis: UnreachableRedis, configure: services =>
            ReplaceCheck(services, HealthCheckEndpoints.SqlServerCheckName, sql));

        using var response = await factory.CreateClient().GetAsync(HealthCheckEndpoints.ReadyPath);
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Degraded", body.RootElement.GetProperty("checks").GetProperty("redis").GetProperty("status").GetString());
    }

    [Fact]
    public async Task HealthEndpoints_AreNotRateLimited_AndAreNotCached()
    {
        await using var factory = CreateFactory();
        factory.Settings["RateLimiting:Mutations:PermitLimit"] = "1";
        factory.Settings["RateLimiting:Analytics:PermitLimit"] = "1";
        var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            using var live = await client.GetAsync(HealthCheckEndpoints.LivePath);
            using var ready = await client.GetAsync(HealthCheckEndpoints.ReadyPath);
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.True(ready.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task HealthEndpoints_AnswerHeadButNotPost()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, HealthCheckEndpoints.ReadyPath));
        using var post = await client.PostAsync(HealthCheckEndpoints.LivePath, content: null);

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public void AddApiHealthChecks_WithoutRedis_RegistersOnlyTheSqlCheck()
    {
        var registrations = Registrations(redis: null);

        var sql = Assert.Single(registrations);
        Assert.Equal(HealthCheckEndpoints.SqlServerCheckName, sql.Name);
        Assert.Equal(HealthStatus.Unhealthy, sql.FailureStatus);
        Assert.Equal(HealthCheckEndpoints.SqlServerTimeout, sql.Timeout);
        Assert.Contains(HealthCheckEndpoints.ReadyTag, sql.Tags);
        Assert.IsType<SqlServerHealthCheck>(sql.Factory(new ServiceCollection().BuildServiceProvider()));
    }

    [Fact]
    public void AddApiHealthChecks_WithRedis_AddsADegradingRedisCheck()
    {
        var registrations = Registrations(redis: "localhost:6379");

        var redis = Assert.Single(registrations, r => r.Name == HealthCheckEndpoints.RedisCheckName);
        Assert.Equal(HealthStatus.Degraded, redis.FailureStatus);
        Assert.Equal(HealthCheckEndpoints.RedisTimeout, redis.Timeout);
        Assert.Contains(HealthCheckEndpoints.ReadyTag, redis.Tags);
        Assert.Equal(2, registrations.Count);
    }

    private static IReadOnlyList<HealthCheckRegistration> Registrations(string? redis)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:UrlShortener"] = "Server=unused;Database=unused",
                ["ConnectionStrings:Redis"] = redis,
            })
            .Build();
        using var provider = new ServiceCollection().AddApiHealthChecks(configuration).BuildServiceProvider();
        return provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.ToList();
    }

    private static ApiFactory CreateFactory(string? redis = null, Action<IServiceCollection>? configure = null)
    {
        var factory = new ApiFactory("Production", Now)
        {
            ConfigureServices = services =>
            {
                ReplaceCheck(services, HealthCheckEndpoints.SqlServerCheckName, new FakeHealthCheck(_ => HealthCheckResult.Healthy()));
                configure?.Invoke(services);
            },
        };
        if (redis is not null)
        {
            factory.Settings["ConnectionStrings:Redis"] = redis;
        }

        return factory;
    }

    /// <summary>Keeps the registration (name, tags, failure status) and swaps only the check.</summary>
    private static void ReplaceCheck(IServiceCollection services, string name, IHealthCheck check, TimeSpan? timeout = null) =>
        services.Configure<HealthCheckServiceOptions>(options =>
        {
            var registration = options.Registrations.Single(r => r.Name == name);
            registration.Factory = _ => check;
            if (timeout is { } value)
            {
                registration.Timeout = value;
            }
        });

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static void AssertNoSensitiveDetails(string json)
    {
        foreach (var fragment in new[]
                 {
                     "Secret", "Password", "Server=", "User Id", "Login failed", "Exception", "6379",
                     "description", "exception", "stackTrace", "data", "timeout occurred", "   at ",
                 })
        {
            Assert.DoesNotContain(fragment, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class FakeHealthCheck : IHealthCheck
    {
        private readonly Func<HealthCheckContext, CancellationToken, Task<HealthCheckResult>> _check;
        private int _calls;

        public FakeHealthCheck(Func<HealthCheckContext, HealthCheckResult> check)
            : this((context, _) => Task.FromResult(check(context)))
        {
        }

        public FakeHealthCheck(Func<HealthCheckContext, CancellationToken, Task<HealthCheckResult>> check)
        {
            _check = check;
        }

        public int Calls => Volatile.Read(ref _calls);

        public bool Cancelled { get; private set; }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            try
            {
                return await _check(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }
    }
}
