using System.Net;
using System.Text.Json;
using UrlShortener.Api.Persistence.Entities;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Analytics;

// Runs the real Program pipeline in-process with an InMemory database and a fixed clock.
public sealed class ClickAnalyticsHttpTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Environments => new() { "Development", "Production" };

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task ExistingCode_Returns200WithAnalyticsContract(string environment)
    {
        await using var factory = new ApiFactory(environment, Now);
        var mapping = Mapping("Abc1234");
        mapping.ExpiresAtUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedAsync(factory, mapping);
        await SeedClicksAsync(
            factory,
            Click(mapping, Hash(1), "DE"),
            Click(mapping, null, null),
            Click(mapping, null, null));

        using var response = await factory.CreateClient().GetAsync("/api/v1/analytics/Abc1234");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal("Abc1234", root.GetProperty("shortCode").GetString());
        Assert.Equal("active", root.GetProperty("linkStatus").GetString());
        Assert.Equal("2030-01-01T00:00:00Z", root.GetProperty("expiresAtUtc").GetString());
        Assert.Equal(3, root.GetProperty("totalClicks").GetInt64());
        var visitors = root.GetProperty("uniqueVisitors");
        Assert.Equal("partial", visitors.GetProperty("status").GetString());
        Assert.Equal(1, visitors.GetProperty("count").GetInt64());
        Assert.Equal(1, visitors.GetProperty("clicksWithVisitorHash").GetInt64());
        var country = Assert.Single(root.GetProperty("countries").EnumerateArray());
        Assert.Equal("DE", country.GetProperty("countryCode").GetString());
        Assert.Equal(1, country.GetProperty("clicks").GetInt64());
        Assert.Equal(2, root.GetProperty("clicksWithUnknownCountry").GetInt64());
    }

    [Fact]
    public async Task NoVisitorHashes_SerializesCountAsExplicitNull()
    {
        await using var factory = new ApiFactory("Production", Now);
        var mapping = Mapping("Abc1234");
        await SeedAsync(factory, mapping);
        await SeedClicksAsync(factory, Click(mapping, null, null));

        var root = await GetJsonAsync(factory, "/api/v1/analytics/Abc1234");

        var visitors = root.GetProperty("uniqueVisitors");
        Assert.Equal("unavailable", visitors.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, visitors.GetProperty("count").ValueKind);
        Assert.Equal(0, visitors.GetProperty("clicksWithVisitorHash").GetInt64());
        Assert.Empty(root.GetProperty("countries").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("expiresAtUtc").ValueKind);
    }

    [Fact]
    public async Task NoClicks_ReportsZeroNotUnavailable()
    {
        await using var factory = new ApiFactory("Production", Now);
        await SeedAsync(factory, Mapping("Abc1234"));

        var root = await GetJsonAsync(factory, "/api/v1/analytics/Abc1234");

        Assert.Equal(0, root.GetProperty("totalClicks").GetInt64());
        Assert.Equal("complete", root.GetProperty("uniqueVisitors").GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("uniqueVisitors").GetProperty("count").GetInt64());
    }

    [Theory]
    [InlineData(false, "inactive")]
    [InlineData(true, "expired")]
    public async Task InactiveOrExpiredCode_Returns200WithStatus(bool isActive, string expectedStatus)
    {
        await using var factory = new ApiFactory("Production", Now);
        var mapping = Mapping("Abc1234");
        mapping.IsActive = isActive;
        mapping.ExpiresAtUtc = Now.UtcDateTime.AddDays(-1);
        await SeedAsync(factory, mapping);
        await SeedClicksAsync(factory, Click(mapping, null, null));

        var root = await GetJsonAsync(factory, "/api/v1/analytics/Abc1234");

        Assert.Equal(expectedStatus, root.GetProperty("linkStatus").GetString());
        Assert.Equal(1, root.GetProperty("totalClicks").GetInt64());
    }

    [Theory]
    [InlineData("Development", "/api/v1/analytics/Zzz9999")]
    [InlineData("Production", "/api/v1/analytics/Zzz9999")]
    [InlineData("Production", "/api/v1/analytics/abc1234")]
    [InlineData("Production", "/api/v1/analytics/ABC1234")]
    [InlineData("Production", "/api/v1/analytics/Abc123")]
    [InlineData("Production", "/api/v1/analytics/Abc12345")]
    [InlineData("Production", "/api/v1/analytics/Abc-123")]
    [InlineData("Production", "/api/v1/analytics/Abc%2F12")]
    public async Task UnknownMalformedOrWrongCaseCode_Returns404ProblemDetails(string environment, string path)
    {
        await using var factory = new ApiFactory(environment, Now);
        await SeedAsync(factory, Mapping("Abc1234"));

        using var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(problem.RootElement.TryGetProperty("detail", out _));
    }

    private static UrlMapping Mapping(string shortCode) => new()
    {
        ShortCode = shortCode,
        OriginalUrl = "https://example.invalid/",
        OriginalUrlHash = new byte[32],
        IsActive = true,
    };

    private static ClickEvent Click(UrlMapping mapping, byte[]? visitorHash, string? countryCode) => new()
    {
        UrlMappingId = mapping.Id,
        ClickedAtUtc = Now.UtcDateTime,
        VisitorHash = visitorHash,
        CountryCode = countryCode,
    };

    private static byte[] Hash(byte seed)
    {
        var hash = new byte[32];
        Array.Fill(hash, seed);
        return hash;
    }

    private static async Task SeedAsync(ApiFactory factory, UrlMapping mapping)
    {
        await using var context = factory.Database;
        context.UrlMappings.Add(mapping);
        await context.SaveChangesAsync();
    }

    private static async Task SeedClicksAsync(ApiFactory factory, params ClickEvent[] clicks)
    {
        await using var context = factory.Database;
        context.ClickEvents.AddRange(clicks);
        await context.SaveChangesAsync();
    }

    private static async Task<JsonElement> GetJsonAsync(ApiFactory factory, string path)
    {
        using var response = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
