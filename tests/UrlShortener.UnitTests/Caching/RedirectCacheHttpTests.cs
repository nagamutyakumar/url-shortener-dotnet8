using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Caching;
using UrlShortener.Api.Persistence.Entities;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Caching;

// Runs the real Program pipeline in-process with InMemory SQL, a fixed clock, a recording click
// queue, and either FakeRedirectCache or a real StackExchange.Redis client pointed at a port where
// nothing listens. Redirects are never followed.
public sealed class RedirectCacheHttpTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeRedirectCache _cache = new();
    private readonly RecordingClickEventQueue _queue = new();

    [Fact]
    public async Task CacheHit_Redirects302AndStillQueuesClick()
    {
        await using var factory = CreateFactory();
        _cache.Entries["Cache01"] = new CachedRedirect(77, "https://example.invalid/cached", null);

        using var response = await GetAsync(factory, "/Cache01");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.invalid/cached", response.Headers.Location?.OriginalString);
        Assert.Equal(77, Assert.Single(_queue.Messages).UrlMappingId);
    }

    [Fact]
    public async Task MissThenHit_BothRedirectAndQueueClicksForTheSameMapping()
    {
        await using var factory = CreateFactory();
        var mapping = await SeedAsync(factory, Mapping("Abc1234"));

        using var first = await GetAsync(factory, "/Abc1234");
        using var second = await GetAsync(factory, "/Abc1234");

        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        Assert.Equal(HttpStatusCode.Found, second.StatusCode);
        Assert.Single(_cache.Sets);
        Assert.Equal(new[] { mapping.Id, mapping.Id }, _queue.Messages.Select(m => m.UrlMappingId));
    }

    [Fact]
    public async Task WrongCaseCode_Returns404EvenWhenTheOtherCaseIsCached()
    {
        await using var factory = CreateFactory();
        await SeedAsync(factory, Mapping("Abc1234"));
        using (await GetAsync(factory, "/Abc1234"))
        {
        }

        using var response = await GetAsync(factory, "/abc1234");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(_queue.Messages);
    }

    [Fact]
    public async Task Put_InvalidatesSoNextRedirectUsesNewUrl()
    {
        await using var factory = CreateFactory();
        await SeedAsync(factory, Mapping("Abc1234"));
        using (await GetAsync(factory, "/Abc1234"))
        {
        }

        using var put = await PutAsync(factory, "Abc1234", """{"originalUrl":"https://example.invalid/new"}""");
        using var redirect = await GetAsync(factory, "/Abc1234");

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(new[] { "Abc1234" }, _cache.Removes);
        Assert.Equal("https://example.invalid/new", redirect.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Put_ShorterExpiration_NextFillUsesShorterTtl()
    {
        await using var factory = CreateFactory();
        await SeedAsync(factory, Mapping("Abc1234"));
        using (await GetAsync(factory, "/Abc1234"))
        {
        }

        using var put = await PutAsync(
            factory, "Abc1234", """{"originalUrl":"https://example.invalid/new","expiresAtUtc":"2026-10-08T12:00:10Z"}""");
        using (await GetAsync(factory, "/Abc1234"))
        {
        }

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(10), _cache.Sets.Last().TimeToLive);
        factory.TimeProvider.UtcNow = Now.AddSeconds(10);
        using var expired = await GetAsync(factory, "/Abc1234");
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
    }

    [Fact]
    public async Task Delete_InvalidatesSoRedirectStopsAndNoClickIsQueued()
    {
        await using var factory = CreateFactory();
        await SeedAsync(factory, Mapping("Abc1234"));
        using (await GetAsync(factory, "/Abc1234"))
        {
        }

        using var delete = await Client(factory).DeleteAsync("/api/v1/shorten/Abc1234");
        using var redirect = await GetAsync(factory, "/Abc1234");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, redirect.StatusCode);
        Assert.Single(_queue.Messages);
        Assert.Single(_cache.Sets);
    }

    [Fact]
    public async Task InvalidationFails_WriteStillSucceeds_AndStaleEntryIsServedUntilItExpires()
    {
        // The documented limitation: a lost invalidation leaves the old entry for at most its TTL.
        await using var factory = CreateFactory();
        await SeedAsync(factory, Mapping("Abc1234"));
        using (await GetAsync(factory, "/Abc1234"))
        {
        }

        _cache.FailRemoves = true;
        using var put = await PutAsync(factory, "Abc1234", """{"originalUrl":"https://example.invalid/new"}""");
        using var delete = await Client(factory).DeleteAsync("/api/v1/shorten/Abc1234");
        using var stale = await GetAsync(factory, "/Abc1234");

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await using (var context = factory.Database)
        {
            var stored = await context.UrlMappings.AsNoTracking().SingleAsync();
            Assert.False(stored.IsActive);
            Assert.Equal("https://example.invalid/new", stored.OriginalUrl);
        }

        Assert.Equal("https://example.invalid/old", stale.Headers.Location?.OriginalString);
        Assert.Equal(TimeSpan.FromMinutes(5), Assert.Single(_cache.Sets).TimeToLive);

        // A repeated DELETE once Redis is back repairs it.
        _cache.FailRemoves = false;
        using var retry = await Client(factory).DeleteAsync("/api/v1/shorten/Abc1234");
        using var repaired = await GetAsync(factory, "/Abc1234");
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, repaired.StatusCode);
    }

    [Fact]
    public async Task CacheLookupAndFillFail_RedirectsFromSqlInsteadOf500()
    {
        await using var factory = CreateFactory();
        var mapping = await SeedAsync(factory, Mapping("Abc1234"));
        _cache.FailGets = true;
        _cache.FailSets = true;

        using var response = await GetAsync(factory, "/Abc1234");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.invalid/old", response.Headers.Location?.OriginalString);
        Assert.Equal(mapping.Id, Assert.Single(_queue.Messages).UrlMappingId);
    }

    [Fact]
    public async Task RedisNotConfigured_UsesNullCache()
    {
        await using var factory = new ApiFactory("Development", Now);

        Assert.Same(NullRedirectCache.Instance, factory.Services.GetRequiredService<IRedirectCache>());
    }

    [Fact]
    public async Task RedisConfiguredButUnreachable_AppStartsAndAllEndpointsWorkFromSql()
    {
        // Real StackExchange.Redis client; nothing listens on port 1. AbortOnConnectFail=false lets
        // the app start, and FailFast makes the failed DELETE invalidation return promptly.
        await using var factory = new ApiFactory("Production", Now)
        {
            ConfigureServices = s => ReplaceQueue(s, _queue),
        };
        factory.Settings["ConnectionStrings:Redis"] = "127.0.0.1:1,connectTimeout=250,connectRetry=0";
        await SeedAsync(factory, Mapping("Abc1234"));
        Assert.IsType<RedisRedirectCache>(factory.Services.GetRequiredService<IRedirectCache>());

        using var redirect = await GetAsync(factory, "/Abc1234");
        using var put = await PutAsync(factory, "Abc1234", """{"originalUrl":"https://example.invalid/new"}""");
        using var redirectAfterPut = await GetAsync(factory, "/Abc1234");
        using var delete = await Client(factory).DeleteAsync("/api/v1/shorten/Abc1234");
        using var redirectAfterDelete = await GetAsync(factory, "/Abc1234");

        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("https://example.invalid/new", redirectAfterPut.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, redirectAfterDelete.StatusCode);
        Assert.Equal(2, _queue.Messages.Count);
    }

    private ApiFactory CreateFactory() => new("Production", Now)
    {
        ConfigureServices = services =>
        {
            ReplaceQueue(services, _queue);
            services.RemoveAll<IRedirectCache>();
            services.AddSingleton<IRedirectCache>(_cache);
        },
    };

    private static void ReplaceQueue(IServiceCollection services, IClickEventQueue queue)
    {
        services.RemoveAll<IClickEventQueue>();
        services.AddSingleton(queue);
    }

    private static UrlMapping Mapping(string shortCode) => new()
    {
        ShortCode = shortCode,
        OriginalUrl = "https://example.invalid/old",
        OriginalUrlHash = new byte[32],
        IsActive = true,
    };

    private static HttpClient Client(ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static Task<HttpResponseMessage> GetAsync(ApiFactory factory, string path) => Client(factory).GetAsync(path);

    private static Task<HttpResponseMessage> PutAsync(ApiFactory factory, string code, string json) =>
        Client(factory).PutAsync($"/api/v1/shorten/{code}", new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<UrlMapping> SeedAsync(ApiFactory factory, UrlMapping mapping)
    {
        await using var context = factory.Database;
        context.UrlMappings.Add(mapping);
        await context.SaveChangesAsync();
        return mapping;
    }
}
