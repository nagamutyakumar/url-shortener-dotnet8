using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Persistence.Entities;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Redirection;

// Runs the real Program pipeline in-process. Most cases swap the queue for a test double so
// they are deterministic; the end-to-end cases poll the database for the background writer.
public sealed class RedirectClickTrackingHttpTests
{
    private const string Secret = "test-secret-0123456789-0123456789-abcdef";
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActiveCode_Redirects302AndQueuesOneClick()
    {
        var queue = new RecordingClickEventQueue();
        await using var factory = new ApiFactory("Production", Now) { ConfigureServices = s => Replace(s, queue) };
        var mapping = await SeedAsync(factory, Mapping("Abc1234"));

        using var response = await GetAsync(factory, "/Abc1234");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var message = Assert.Single(queue.Messages);
        Assert.Equal(mapping.Id, message.UrlMappingId);
        Assert.Equal(Now.UtcDateTime, message.ClickedAtUtc);
        // No VisitorHashSecret configured in this test.
        Assert.Null(message.VisitorHash);
    }

    [Theory]
    [InlineData("/Zzz9999")]
    [InlineData("/abc1234")]
    [InlineData("/Abc-123")]
    [InlineData("/Inact01")]
    [InlineData("/Expir01")]
    public async Task NonRedirectingCode_Returns404AndQueuesNothing(string path)
    {
        var queue = new RecordingClickEventQueue();
        await using var factory = new ApiFactory("Production", Now) { ConfigureServices = s => Replace(s, queue) };
        var inactive = Mapping("Inact01");
        inactive.IsActive = false;
        var expired = Mapping("Expir01");
        expired.ExpiresAtUtc = Now.UtcDateTime;
        await SeedAsync(factory, Mapping("Abc1234"), inactive, expired);

        using var response = await GetAsync(factory, path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(queue.Messages);
    }

    [Fact]
    public async Task EnqueueThrows_StillRedirects302()
    {
        var queue = new ThrowingClickEventQueue();
        await using var factory = new ApiFactory("Production", Now) { ConfigureServices = s => Replace(s, queue) };
        await SeedAsync(factory, Mapping("Abc1234"));

        using var response = await GetAsync(factory, "/Abc1234");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.invalid/target", response.Headers.Location?.OriginalString);
        Assert.Equal(1, queue.Calls);
    }

    [Fact]
    public async Task QueueFull_StillRedirects302AndCountsDroppedClicks()
    {
        // Nothing drains the queue, so it stays full after the first click.
        await using var factory = new ApiFactory("Production", Now) { RunClickEventWriter = false };
        factory.Settings["Analytics:QueueCapacity"] = "1";
        await SeedAsync(factory, Mapping("Abc1234"));

        for (var i = 0; i < 3; i++)
        {
            using var response = await GetAsync(factory, "/Abc1234");
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }

        var queue = factory.Services.GetRequiredService<ClickEventQueue>();
        Assert.Equal(1, queue.Reader.Count);
        Assert.Equal(2, queue.DroppedCount);
    }

    [Fact]
    public async Task Redirect_IsPersistedByBackgroundWriter_AndVisibleInAnalytics()
    {
        await using var factory = new ApiFactory("Production", Now);
        factory.Settings["Analytics:VisitorHashSecret"] = Secret;
        var mapping = await SeedAsync(factory, Mapping("Abc1234"));

        using (var response = await GetAsync(factory, "/Abc1234"))
        {
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }

        await WaitUntilAsync(async () => await CountClicksAsync(factory) == 1);

        await using (var context = factory.Database)
        {
            var click = await context.ClickEvents.SingleAsync();
            Assert.Equal(mapping.Id, click.UrlMappingId);
            Assert.Equal(Now.UtcDateTime, click.ClickedAtUtc);
            Assert.Null(click.CountryCode);
            // TestServer supplies no remote IP address, and without one no visitor hash is
            // computed even though a secret is configured (see VisitorHasher).
            Assert.Null(click.VisitorHash);
        }

        using var analytics = await GetAsync(factory, "/api/v1/analytics/Abc1234");
        Assert.Equal(HttpStatusCode.OK, analytics.StatusCode);
        Assert.Contains("\"totalClicks\":1", await analytics.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PersistenceFails_RedirectsStill302_AndWriterKeepsRunning()
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234"));
        // Only the app's DbContext has this interceptor; seeding above is unaffected.
        factory.Interceptor.FailForever = true;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var response = await GetAsync(factory, "/Abc1234");
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);

            // The writer has tried, and failed, to insert this click; the next one still works.
            await WaitUntilAsync(() => Task.FromResult(factory.Interceptor.Attempts >= attempt));
        }

        Assert.Equal(0, await CountClicksAsync(factory));
    }

    private static void Replace(IServiceCollection services, IClickEventQueue queue)
    {
        services.RemoveAll<IClickEventQueue>();
        services.AddSingleton(queue);
    }

    private static UrlMapping Mapping(string shortCode) => new()
    {
        ShortCode = shortCode,
        OriginalUrl = "https://example.invalid/target",
        OriginalUrlHash = new byte[32],
        IsActive = true,
    };

    private static async Task<UrlMapping> SeedAsync(ApiFactory factory, params UrlMapping[] mappings)
    {
        await using var context = factory.Database;
        context.UrlMappings.AddRange(mappings);
        await context.SaveChangesAsync();
        return mappings[0];
    }

    private static async Task<int> CountClicksAsync(ApiFactory factory)
    {
        await using var context = factory.Database;
        return await context.ClickEvents.CountAsync();
    }

    private static Task<HttpResponseMessage> GetAsync(ApiFactory factory, string path) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetAsync(path);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met within 10 seconds.");
            }

            await Task.Delay(20);
        }
    }
}
