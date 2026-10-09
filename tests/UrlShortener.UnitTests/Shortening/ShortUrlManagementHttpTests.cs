using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Persistence.Entities;
using UrlShortener.Api.Shortening;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Shortening;

// Runs the real Program pipeline (routing, JSON binding, status code pages) in-process with
// an InMemory database and a fixed clock. Redirects are never followed.
public sealed class ShortUrlManagementHttpTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Environments => new() { "Development", "Production" };

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task Put_ValidBody_Returns200AndRedirectUsesNewUrl(string environment)
    {
        await using var factory = new ApiFactory(environment, Now);
        await SeedAsync(factory, Mapping("Abc1234"));

        using var response = await PutAsync(
            factory, "Abc1234", """{"originalUrl":"HTTPS://Example.invalid:443/New?q=1","expiresAtUtc":"2030-01-01T05:30:00.1239+05:30"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal("Abc1234", root.GetProperty("shortCode").GetString());
        Assert.EndsWith("/Abc1234", root.GetProperty("shortUrl").GetString());
        Assert.Equal("https://example.invalid/New?q=1", root.GetProperty("originalUrl").GetString());
        Assert.Equal("2030-01-01T00:00:00.123Z", root.GetProperty("expiresAtUtc").GetString());

        using var redirect = await Client(factory).GetAsync("/Abc1234");
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal("https://example.invalid/New?q=1", redirect.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Put_OmittedExpiration_PreservesIt()
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234", expiresAtUtc: new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        using var response = await PutAsync(factory, "Abc1234", """{"originalUrl":"https://example.invalid/new"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), (await ReadAsync(factory, "Abc1234")).ExpiresAtUtc);
    }

    [Fact]
    public async Task Put_ExplicitNullExpiration_ClearsIt()
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234", expiresAtUtc: new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        using var response = await PutAsync(
            factory, "Abc1234", """{"originalUrl":"https://example.invalid/new","expiresAtUtc":null}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("expiresAtUtc").ValueKind);
        Assert.Null((await ReadAsync(factory, "Abc1234")).ExpiresAtUtc);
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task Put_InvalidValues_Returns400FieldErrorsAndChangesNothing(string environment)
    {
        await using var factory = new ApiFactory(environment, Now);
        await SeedAsync(factory, Mapping("Abc1234"));

        using var response = await PutAsync(
            factory, "Abc1234", """{"originalUrl":"javascript:alert(1)","expiresAtUtc":"2026-10-08T11:59:59Z"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = problem.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty(nameof(UpdateShortUrlRequest.OriginalUrl), out _));
        Assert.Equal(
            "The expiration must be in the future.",
            errors.GetProperty(nameof(UpdateShortUrlRequest.ExpiresAtUtc))[0].GetString());
        Assert.Equal("https://example.invalid/old", (await ReadAsync(factory, "Abc1234")).OriginalUrl);
    }

    [Theory]
    [InlineData("""{"expiresAtUtc":null}""")]
    [InlineData("""{"originalUrl":null}""")]
    [InlineData("""{}""")]
    public async Task Put_MissingUrl_Returns400(string json)
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234"));

        using var response = await PutAsync(factory, "Abc1234", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(nameof(UpdateShortUrlRequest.OriginalUrl), out _));
    }

    [Theory]
    [InlineData("""not json""")]
    [InlineData("""{"originalUrl":"https://example.invalid/","expiresAtUtc":"2030-01-01T00:00:00"}""")]
    [InlineData("""{"originalUrl":"https://example.invalid/","expiresAtUtc":"tomorrow"}""")]
    public async Task Put_UnreadableBody_Returns400ProblemDetails(string json)
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234"));

        using var response = await PutAsync(factory, "Abc1234", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("abc1234")]
    [InlineData("ABC1234")]
    [InlineData("Zzz9999")]
    [InlineData("Abc123")]
    [InlineData("Abc-123")]
    [InlineData("Abc%2F12")]
    public async Task Put_UnknownMalformedOrWrongCaseCode_Returns404AndChangesNothing(string code)
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234"));

        using var response = await PutAsync(factory, code, """{"originalUrl":"https://example.invalid/new"}""");

        await AssertNotFoundAsync(response);
        Assert.Equal("https://example.invalid/old", (await ReadAsync(factory, "Abc1234")).OriginalUrl);
    }

    [Fact]
    public async Task Put_InactiveCode_Returns404AndStaysDeleted()
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234", isActive: false));

        using var response = await PutAsync(
            factory, "Abc1234", """{"originalUrl":"https://example.invalid/new","expiresAtUtc":null}""");

        await AssertNotFoundAsync(response);
        using var redirect = await Client(factory).GetAsync("/Abc1234");
        Assert.Equal(HttpStatusCode.NotFound, redirect.StatusCode);
    }

    [Fact]
    public async Task Put_ExpiredCodeWithNewExpiration_RedirectsAgain()
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234", expiresAtUtc: Now.UtcDateTime.AddDays(-1)));

        using var response = await PutAsync(
            factory, "Abc1234", """{"originalUrl":"https://example.invalid/new","expiresAtUtc":"2030-01-01T00:00:00Z"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var redirect = await Client(factory).GetAsync("/Abc1234");
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
    }

    [Fact]
    public async Task Put_PreservesClickHistory()
    {
        await using var factory = new ApiFactory("Production", Now);
        var mapping = await SeedAsync(factory, Mapping("Abc1234"));
        await SeedClicksAsync(factory, mapping.Id, 2);

        using var response = await PutAsync(factory, "Abc1234", """{"originalUrl":"https://example.invalid/new"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var analytics = await GetAnalyticsAsync(factory, "Abc1234");
        Assert.Equal(2, analytics.GetProperty("totalClicks").GetInt64());
        Assert.Equal("active", analytics.GetProperty("linkStatus").GetString());
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task Delete_ActiveCode_Returns204StopsRedirectAndKeepsAnalytics(string environment)
    {
        await using var factory = new ApiFactory(environment, Now);
        var mapping = await SeedAsync(factory, Mapping("Abc1234"));
        await SeedClicksAsync(factory, mapping.Id, 3);

        using var response = await Client(factory).DeleteAsync("/api/v1/shorten/Abc1234");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        using var redirect = await Client(factory).GetAsync("/Abc1234");
        await AssertNotFoundAsync(redirect);
        var analytics = await GetAnalyticsAsync(factory, "Abc1234");
        Assert.Equal("inactive", analytics.GetProperty("linkStatus").GetString());
        Assert.Equal(3, analytics.GetProperty("totalClicks").GetInt64());
    }

    [Fact]
    public async Task Delete_Repeated_Returns204WithoutWritingAgain()
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234"));

        using var first = await Client(factory).DeleteAsync("/api/v1/shorten/Abc1234");
        var afterFirst = await ReadAsync(factory, "Abc1234");
        factory.TimeProvider.UtcNow = Now.AddMinutes(5);
        using var second = await Client(factory).DeleteAsync("/api/v1/shorten/Abc1234");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        var afterSecond = await ReadAsync(factory, "Abc1234");
        Assert.False(afterSecond.IsActive);
        Assert.Equal(afterFirst.RowVersion, afterSecond.RowVersion);
        Assert.Equal(Now.UtcDateTime, afterSecond.UpdatedAtUtc);
    }

    [Fact]
    public async Task Delete_ExpiredCode_Returns204()
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234", expiresAtUtc: Now.UtcDateTime.AddDays(-1)));

        using var response = await Client(factory).DeleteAsync("/api/v1/shorten/Abc1234");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False((await ReadAsync(factory, "Abc1234")).IsActive);
    }

    [Theory]
    [InlineData("abc1234")]
    [InlineData("ABC1234")]
    [InlineData("Zzz9999")]
    [InlineData("Abc12345")]
    [InlineData("Abc%20123")]
    public async Task Delete_UnknownMalformedOrWrongCaseCode_Returns404AndChangesNothing(string code)
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234"));

        using var response = await Client(factory).DeleteAsync($"/api/v1/shorten/{code}");

        await AssertNotFoundAsync(response);
        Assert.True((await ReadAsync(factory, "Abc1234")).IsActive);
    }

    [Fact]
    public async Task Delete_CodesDifferingOnlyInCase_DeletesOnlyTheExactMatch()
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("abc1234"));
        await SeedAsync(factory, Mapping("ABC1234"));

        using var response = await Client(factory).DeleteAsync("/api/v1/shorten/ABC1234");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True((await ReadAsync(factory, "abc1234")).IsActive);
        Assert.False((await ReadAsync(factory, "ABC1234")).IsActive);
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task ConcurrencyConflict_Returns409ProblemDetails(string environment)
    {
        await using var factory = new ApiFactory(environment, Now);
        await SeedAsync(factory, Mapping("Abc1234"));
        factory.Interceptor.Enqueue(new DbUpdateConcurrencyException("Simulated rowversion mismatch."));
        factory.Interceptor.Enqueue(new DbUpdateConcurrencyException("Simulated rowversion mismatch."));

        using var put = await PutAsync(factory, "Abc1234", """{"originalUrl":"https://example.invalid/new"}""");
        using var delete = await Client(factory).DeleteAsync("/api/v1/shorten/Abc1234");

        foreach (var response in new[] { put, delete })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(409, problem.RootElement.GetProperty("status").GetInt32());
        }

        var stored = await ReadAsync(factory, "Abc1234");
        Assert.True(stored.IsActive);
        Assert.Equal("https://example.invalid/old", stored.OriginalUrl);
    }

    [Fact]
    public async Task UnexpectedDatabaseFailure_Returns500()
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234"));
        factory.Interceptor.Enqueue(new DbUpdateException("Simulated database failure."));

        using var response = await PutAsync(factory, "Abc1234", """{"originalUrl":"https://example.invalid/new"}""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private static UrlMapping Mapping(string shortCode, DateTime? expiresAtUtc = null, bool isActive = true) => new()
    {
        ShortCode = shortCode,
        OriginalUrl = "https://example.invalid/old",
        OriginalUrlHash = UrlCanonicalizer.ComputeHash("https://example.invalid/old"),
        ExpiresAtUtc = expiresAtUtc,
        IsActive = isActive,
    };

    private static HttpClient Client(ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static Task<HttpResponseMessage> PutAsync(ApiFactory factory, string code, string json) =>
        Client(factory).PutAsync(
            $"/api/v1/shorten/{code}", new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<UrlMapping> SeedAsync(ApiFactory factory, UrlMapping mapping)
    {
        await using var context = factory.Database;
        context.UrlMappings.Add(mapping);
        await context.SaveChangesAsync();
        return mapping;
    }

    private static async Task SeedClicksAsync(ApiFactory factory, long urlMappingId, int count)
    {
        await using var context = factory.Database;
        for (var i = 0; i < count; i++)
        {
            context.ClickEvents.Add(new ClickEvent { UrlMappingId = urlMappingId, ClickedAtUtc = Now.UtcDateTime });
        }

        await context.SaveChangesAsync();
    }

    private static async Task<UrlMapping> ReadAsync(ApiFactory factory, string code)
    {
        await using var context = factory.Database;
        return await context.UrlMappings.AsNoTracking().SingleAsync(m => m.ShortCode == code);
    }

    private static async Task<JsonElement> GetAnalyticsAsync(ApiFactory factory, string code)
    {
        using var response = await Client(factory).GetAsync($"/api/v1/analytics/{code}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task AssertNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(problem.RootElement.TryGetProperty("detail", out _));
    }
}
