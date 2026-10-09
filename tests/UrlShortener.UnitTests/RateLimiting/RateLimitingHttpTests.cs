using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Persistence.Entities;
using UrlShortener.Api.Shortening;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.RateLimiting;

// Runs the real Program pipeline with low limits and a 1-hour window, so no window can roll over
// mid-test and nothing waits on the clock. Each factory has its own limiter state.
public sealed class RateLimitingHttpTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Test-only header that sets Connection.RemoteIpAddress (see <see cref="ClientIpStartupFilter"/>).</summary>
    private const string TestClientIpHeader = "X-Test-Client-IP";

    [Fact]
    public async Task Mutations_WithinLimit_Succeed_ThenReturn429ProblemDetailsWithRetryAfter()
    {
        await using var factory = CreateFactory(mutations: 2, analytics: 100);

        using var first = await PostAsync(factory);
        using var second = await PostAsync(factory);
        using var rejected = await PostAsync(factory);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var retryAfter = Assert.Single(rejected.Headers.GetValues("Retry-After"));
        Assert.InRange(int.Parse(retryAfter), 1, 3600);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        var json = await rejected.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(json);
        Assert.Equal("https://tools.ietf.org/html/rfc6585#section-4", problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(429, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Too many requests.", problem.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("mutations", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PermitLimit", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostPutAndDelete_ShareOneBudget_AndRejectedRequestsChangeNothing()
    {
        await using var factory = CreateFactory(mutations: 3, analytics: 100);
        await SeedAsync(factory, Mapping("Abc1234"));
        await SeedAsync(factory, Mapping("Def5678"));

        using var post = await PostAsync(factory);
        using var put = await PutAsync(factory, "Abc1234");
        using var delete = await SendAsync(factory, HttpMethod.Delete, "/api/v1/shorten/Def5678");
        using var rejectedPost = await PostAsync(factory);
        using var rejectedPut = await PutAsync(factory, "Def5678");
        using var rejectedDelete = await SendAsync(factory, HttpMethod.Delete, "/api/v1/shorten/Abc1234");

        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedPost.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedPut.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedDelete.StatusCode);
        await using var context = factory.Database;
        Assert.True(context.UrlMappings.Single(m => m.ShortCode == "Abc1234").IsActive);
        Assert.Equal(2, context.UrlMappings.Count(m => m.IsActive));
    }

    [Fact]
    public async Task Analytics_HasItsOwnBudget_IndependentOfMutations()
    {
        await using var factory = CreateFactory(mutations: 1, analytics: 2);
        await SeedAsync(factory, Mapping("Abc1234"));

        using var analytics1 = await SendAsync(factory, HttpMethod.Get, "/api/v1/analytics/Abc1234");
        using var analytics2 = await SendAsync(factory, HttpMethod.Get, "/api/v1/analytics/Abc1234");
        using var analyticsRejected = await SendAsync(factory, HttpMethod.Get, "/api/v1/analytics/Abc1234");
        using var post = await PostAsync(factory);
        using var postRejected = await PostAsync(factory);

        Assert.Equal(HttpStatusCode.OK, analytics1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, analytics2.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, analyticsRejected.StatusCode);
        Assert.True(analyticsRejected.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, postRejected.StatusCode);
    }

    [Fact]
    public async Task DifferentClientIps_HaveSeparateBudgets_AndIpv4MappedIpv6IsTheSameClient()
    {
        await using var factory = CreateFactory(mutations: 1, analytics: 100);

        using var clientA = await PostAsync(factory, clientIp: "203.0.113.10");
        using var clientARejected = await PostAsync(factory, clientIp: "203.0.113.10");
        using var clientAMapped = await PostAsync(factory, clientIp: "::ffff:203.0.113.10");
        using var clientB = await PostAsync(factory, clientIp: "203.0.113.20");
        using var clientIpv6 = await PostAsync(factory, clientIp: "2001:db8::1");

        Assert.Equal(HttpStatusCode.Created, clientA.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, clientARejected.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, clientAMapped.StatusCode);
        Assert.Equal(HttpStatusCode.OK, clientB.StatusCode);
        Assert.Equal(HttpStatusCode.OK, clientIpv6.StatusCode);
    }

    [Fact]
    public async Task ForwardedForHeader_IsIgnored()
    {
        await using var factory = CreateFactory(mutations: 1, analytics: 100);

        using var first = await PostAsync(factory, clientIp: "203.0.113.10", forwardedFor: "198.51.100.1");
        using var spoofed = await PostAsync(factory, clientIp: "203.0.113.10", forwardedFor: "198.51.100.2");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, spoofed.StatusCode);
    }

    [Fact]
    public async Task NoRemoteIp_UsesOneSharedFallbackPartition()
    {
        await using var factory = CreateFactory(mutations: 1, analytics: 100);

        using var first = await PostAsync(factory);
        using var second = await PostAsync(factory);
        using var withIp = await PostAsync(factory, clientIp: "203.0.113.10");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withIp.StatusCode);
    }

    [Fact]
    public async Task RedirectsAndSwagger_AreNotLimited_EvenWhenBothBudgetsAreExhausted()
    {
        await using var factory = CreateFactory(mutations: 1, analytics: 1, environment: "Development");
        await SeedAsync(factory, Mapping("Abc1234"));
        using var allowedPost = await PostAsync(factory);
        using var allowedAnalytics = await SendAsync(factory, HttpMethod.Get, "/api/v1/analytics/Abc1234");
        using var mutationRejected = await PostAsync(factory);
        using var analyticsRejected = await SendAsync(factory, HttpMethod.Get, "/api/v1/analytics/Abc1234");
        Assert.Equal(HttpStatusCode.Created, allowedPost.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowedAnalytics.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, mutationRejected.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, analyticsRejected.StatusCode);

        for (var i = 0; i < 10; i++)
        {
            using var redirect = await SendAsync(factory, HttpMethod.Get, "/Abc1234");
            Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
            Assert.False(redirect.Headers.Contains("Retry-After"));
        }

        for (var i = 0; i < 3; i++)
        {
            using var swagger = await SendAsync(factory, HttpMethod.Get, "/swagger/v1/swagger.json");
            Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
        }
    }

    [Theory]
    [InlineData("RateLimiting:Mutations:PermitLimit", "0")]
    [InlineData("RateLimiting:Mutations:PermitLimit", "-1")]
    [InlineData("RateLimiting:Mutations:Window", "00:00:00")]
    [InlineData("RateLimiting:Analytics:PermitLimit", "0")]
    [InlineData("RateLimiting:Analytics:Window", "1.00:00:00")]
    [InlineData("RateLimiting:Analytics:QueueLimit", "-1")]
    public async Task InvalidConfiguration_FailsAtStartup(string key, string value)
    {
        await using var factory = new ApiFactory("Production", Now);
        factory.Settings[key] = value;

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("RateLimiting", exception.Message);
    }

    private static ApiFactory CreateFactory(int mutations, int analytics, string environment = "Production")
    {
        var factory = new ApiFactory(environment, Now)
        {
            ConfigureServices = services => services.AddSingleton<IStartupFilter, ClientIpStartupFilter>(),
        };
        factory.Settings["RateLimiting:Mutations:PermitLimit"] = mutations.ToString();
        factory.Settings["RateLimiting:Mutations:Window"] = "01:00:00";
        factory.Settings["RateLimiting:Analytics:PermitLimit"] = analytics.ToString();
        factory.Settings["RateLimiting:Analytics:Window"] = "01:00:00";
        return factory;
    }

    private static Task<HttpResponseMessage> PostAsync(ApiFactory factory, string? clientIp = null, string? forwardedFor = null) =>
        SendAsync(
            factory,
            HttpMethod.Post,
            ShortenEndpoint.Route,
            """{"originalUrl":"https://example.invalid/page"}""",
            clientIp,
            forwardedFor);

    private static Task<HttpResponseMessage> PutAsync(ApiFactory factory, string code) =>
        SendAsync(factory, HttpMethod.Put, $"/api/v1/shorten/{code}", """{"originalUrl":"https://example.invalid/new"}""");

    private static Task<HttpResponseMessage> SendAsync(
        ApiFactory factory,
        HttpMethod method,
        string path,
        string? json = null,
        string? clientIp = null,
        string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (clientIp is not null)
        {
            request.Headers.Add(TestClientIpHeader, clientIp);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return client.SendAsync(request);
    }

    private static UrlMapping Mapping(string shortCode) => new()
    {
        ShortCode = shortCode,
        OriginalUrl = "https://example.invalid/old",
        OriginalUrlHash = UrlCanonicalizer.ComputeHash("https://example.invalid/old"),
        IsActive = true,
    };

    private static async Task SeedAsync(ApiFactory factory, UrlMapping mapping)
    {
        await using var context = factory.Database;
        context.UrlMappings.Add(mapping);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// TestServer leaves RemoteIpAddress null. This sets it from a test-only header before the
    /// app's own middleware runs, standing in for the socket address a real server would supply.
    /// </summary>
    private sealed class ClientIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(TestClientIpHeader, out var value))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(value.ToString());
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
