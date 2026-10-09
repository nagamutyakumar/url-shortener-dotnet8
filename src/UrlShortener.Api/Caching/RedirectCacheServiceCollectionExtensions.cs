using StackExchange.Redis;

namespace UrlShortener.Api.Caching;

public static class RedirectCacheServiceCollectionExtensions
{
    public const string ConnectionStringName = "Redis";

    /// <summary>
    /// Registers <see cref="IRedirectCache"/>: Redis when <c>ConnectionStrings:Redis</c> is set,
    /// otherwise <see cref="NullRedirectCache"/>, so the API runs without Redis.
    /// </summary>
    public static IServiceCollection AddRedirectCache(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RedirectCacheOptions>()
            .Bind(configuration.GetSection(RedirectCacheOptions.SectionName))
            .Validate(
                RedirectCacheOptions.IsValid,
                $"{RedirectCacheOptions.SectionName}:MaxTimeToLive must be between " +
                $"{RedirectCacheOptions.MinTimeToLive} and {RedirectCacheOptions.MaxAllowedTimeToLive}.")
            .ValidateOnStart();

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddSingleton<IRedirectCache>(NullRedirectCache.Instance);
            return services;
        }

        // Parsed now so a malformed connection string fails at startup, not on a redirect.
        var redisOptions = CreateConfigurationOptions(connectionString);

        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RedisRedirectCache));
            var multiplexer = ConnectionMultiplexer.Connect(redisOptions);
            if (!multiplexer.IsConnected)
            {
                logger.LogWarning("Redis is not reachable; redirects use SQL Server until it reconnects.");
            }

            // Logged once per transition rather than once per redirect.
            multiplexer.ConnectionFailed += (_, e) => logger.LogWarning(
                e.Exception,
                "Redis connection to {EndPoint} failed ({FailureType}); redirects use SQL Server.",
                e.EndPoint,
                e.FailureType);
            multiplexer.ConnectionRestored += (_, e) => logger.LogInformation(
                "Redis connection to {EndPoint} restored.", e.EndPoint);
            return multiplexer;
        });
        services.AddSingleton<IRedirectCache, RedisRedirectCache>();

        return services;
    }

    /// <summary>
    /// Connects to Redis, when configured, during startup instead of on the first redirect. Does
    /// not throw when Redis is unreachable (see <see cref="CreateConfigurationOptions"/>).
    /// </summary>
    public static void ConnectRedirectCache(this IServiceProvider services) =>
        services.GetService<IConnectionMultiplexer>();

    /// <summary>
    /// The connection string supplies endpoints, credentials and timeouts. Two settings are
    /// forced because the cache must never take the API down with it.
    /// </summary>
    public static ConfigurationOptions CreateConfigurationOptions(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);

        // Start even if Redis is down and keep reconnecting in the background.
        options.AbortOnConnectFail = false;

        // While disconnected, fail commands immediately instead of queueing them until the
        // timeout, so a Redis outage does not add latency to redirects or management calls.
        options.BacklogPolicy = BacklogPolicy.FailFast;

        return options;
    }
}
