using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using UrlShortener.Api.Persistence.Entities;
using UrlShortener.Api.Shortening;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Redirection;

// Runs the real Program pipeline in-process with an InMemory database and a fixed clock.
// The client never follows redirects, so destination URLs are never contacted.
public sealed class RedirectHttpTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Environments => new() { "Development", "Production" };

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task ActiveCode_Returns302WithStoredUrlAsLocation(string environment)
    {
        await using var factory = new ApiFactory(environment, Now);
        await SeedAsync(factory, Mapping("Abc1234", "https://example.invalid/Path/a%20b?q=1&r=%C3%A9#frag"));

        using var response = await GetAsync(factory, "/Abc1234");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(
            "https://example.invalid/Path/a%20b?q=1&r=%C3%A9#frag",
            response.Headers.Location?.OriginalString);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ShortenedUrl_RedirectsToCanonicalUrl()
    {
        await using var factory = new ApiFactory("Production", Now);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var created = await client.PostAsJsonAsync(
            ShortenEndpoint.Route, new { originalUrl = "HTTPS://Example.invalid:443/Docs?page=1" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var shortCode = body.RootElement.GetProperty("shortCode").GetString();

        using var response = await client.GetAsync($"/{shortCode}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.invalid/Docs?page=1", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task UnknownCode_Returns404ProblemDetails(string environment)
    {
        await using var factory = new ApiFactory(environment, Now);
        await SeedAsync(factory, Mapping("Abc1234", "https://example.invalid/"));

        using var response = await GetAsync(factory, "/Zzz9999");

        await AssertNotFoundAsync(response);
    }

    [Theory]
    [InlineData("/Abc123")]
    [InlineData("/Abc12345")]
    [InlineData("/Abc-123")]
    [InlineData("/Abc_123")]
    [InlineData("/Abc%20123")]
    [InlineData("/Abc%2F12")]
    [InlineData("/%C3%A9bc1234")]
    public async Task InvalidCodeFormat_Returns404ProblemDetails(string path)
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234", "https://example.invalid/"));

        using var response = await GetAsync(factory, path);

        await AssertNotFoundAsync(response);
    }

    [Fact]
    public async Task InactiveCode_Returns404()
    {
        await using var factory = new ApiFactory("Production", Now);
        var mapping = Mapping("Abc1234", "https://example.invalid/");
        mapping.IsActive = false;
        await SeedAsync(factory, mapping);

        using var response = await GetAsync(factory, "/Abc1234");

        await AssertNotFoundAsync(response);
    }

    [Fact]
    public async Task ExpirationBoundary_RedirectsUntilExpiresAtThen404()
    {
        await using var factory = new ApiFactory("Production", Now);
        var mapping = Mapping("Abc1234", "https://example.invalid/");
        mapping.ExpiresAtUtc = Now.UtcDateTime.AddMinutes(1);
        await SeedAsync(factory, mapping);

        factory.TimeProvider.UtcNow = Now.AddMinutes(1).AddMilliseconds(-1);
        using (var beforeExpiry = await GetAsync(factory, "/Abc1234"))
        {
            Assert.Equal(HttpStatusCode.Found, beforeExpiry.StatusCode);
        }

        factory.TimeProvider.UtcNow = Now.AddMinutes(1);
        using var atExpiry = await GetAsync(factory, "/Abc1234");
        await AssertNotFoundAsync(atExpiry);
    }

    [Theory]
    [InlineData("/abc1234")]
    [InlineData("/ABC1234")]
    public async Task CodeWithDifferentCase_Returns404(string path)
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234", "https://example.invalid/"));

        using var response = await GetAsync(factory, path);

        await AssertNotFoundAsync(response);
    }

    [Fact]
    public async Task SwaggerUi_InDevelopment_IsNotShadowedByRedirectRoute()
    {
        // "swagger" is itself a well-formed seven-character code.
        await using var factory = new ApiFactory("Development", Now);

        using var response = await GetAsync(factory, "/swagger");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("swagger/index.html", response.Headers.Location?.OriginalString);
    }

    private static UrlMapping Mapping(string shortCode, string originalUrl) => new()
    {
        ShortCode = shortCode,
        OriginalUrl = originalUrl,
        OriginalUrlHash = new byte[32],
        IsActive = true,
    };

    private static async Task SeedAsync(ApiFactory factory, UrlMapping mapping)
    {
        await using var context = factory.Database;
        context.UrlMappings.Add(mapping);
        await context.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> GetAsync(ApiFactory factory, string path) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetAsync(path);

    private static async Task AssertNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
        // Malformed, unknown, inactive and expired codes are indistinguishable to the caller.
        Assert.False(problem.RootElement.TryGetProperty("detail", out _));
        Assert.False(problem.RootElement.TryGetProperty("errors", out _));
    }
}
