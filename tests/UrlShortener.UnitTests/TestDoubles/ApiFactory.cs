using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.Api.Persistence;

namespace UrlShortener.UnitTests.TestDoubles;

/// <summary>
/// Hosts the real Program pipeline in-process with the database swapped for InMemory and
/// the clock fixed.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly string _databaseName = Guid.NewGuid().ToString();

    public ApiFactory(string environment, DateTimeOffset now)
    {
        _environment = environment;
        TimeProvider = new FixedTimeProvider(now);
    }

    public FixedTimeProvider TimeProvider { get; }

    public FailingSaveChangesInterceptor Interceptor { get; } = new();

    /// <summary>A separate context over the same InMemory store, for seeding and assertions.</summary>
    public UrlShortenerDbContext Database => new(
        new DbContextOptionsBuilder<UrlShortenerDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .AddInterceptors(new InMemoryRowVersionInterceptor())
            .Options,
        TimeProvider);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        // Program requires a connection string at startup; it is never used.
        builder.UseSetting("ConnectionStrings:UrlShortener", "Server=unused;Database=unused");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<UrlShortenerDbContext>>();
            services.AddDbContext<UrlShortenerDbContext>(options => options
                .UseInMemoryDatabase(_databaseName)
                .AddInterceptors(Interceptor, new InMemoryRowVersionInterceptor()));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(TimeProvider);
        });
    }
}
