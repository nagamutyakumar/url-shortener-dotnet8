using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Shortening;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Shortening;

// Runs the real Program pipeline (routing, JSON binding, exception handling, status code
// pages) in-process. Only the database is swapped for InMemory and the clock is fixed.
// Each case runs in Development and Production because request-binding failures are
// handled differently depending on the environment.
public sealed class ShortenHttpTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Environments => new() { "Development", "Production" };

    public static TheoryData<string, string> BadBodies
    {
        get
        {
            var data = new TheoryData<string, string>();
            string[] bodies =
            {
                """{"originalUrl":""",
                """not json""",
                """{"originalUrl":"https://example.com/","expiresAtUtc":"2030-01-01T00:00:00"}""",
                """{"originalUrl":"https://example.com/","expiresAtUtc":"2030-01-01"}""",
                """{"originalUrl":"https://example.com/","expiresAtUtc":"tomorrow"}""",
                """{"originalUrl":"https://example.com/","expiresAtUtc":"2030-13-01T00:00:00Z"}""",
                """{"originalUrl":"https://example.com/","expiresAtUtc":"2030-01-01T00:00:00+0530"}""",
                """{"originalUrl":"https://example.com/","expiresAtUtc":1893456000}""",
            };
            foreach (var environment in new[] { "Development", "Production" })
            {
                foreach (var body in bodies)
                {
                    data.Add(environment, body);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task UnreadableBody_Returns400ProblemDetails(string environment, string body)
    {
        await using var factory = new ApiFactory(environment, Now);

        using var response = await PostAsync(factory, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Empty(factory.Database.UrlMappings);
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task PastExpirationWithOffset_Returns400FieldValidationError(string environment)
    {
        await using var factory = new ApiFactory(environment, Now);

        using var response = await PostAsync(
            factory,
            """{"originalUrl":"https://example.com/","expiresAtUtc":"2026-10-08T17:29:59+05:30"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = problem.RootElement.GetProperty("errors").GetProperty(nameof(ShortenUrlRequest.ExpiresAtUtc));
        Assert.Equal("The expiration must be in the future.", errors[0].GetString());
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task ValidRequestWithOffset_Returns201WithUtcExpiration(string environment)
    {
        await using var factory = new ApiFactory(environment, Now);

        using var response = await PostAsync(
            factory,
            """{"originalUrl":"https://example.com/a","expiresAtUtc":"2030-01-01T05:30:00.1239+05:30"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2030-01-01T00:00:00.123Z", body.RootElement.GetProperty("expiresAtUtc").GetString());
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task UnexpectedDatabaseFailure_Returns500(string environment)
    {
        await using var factory = new ApiFactory(environment, Now);
        factory.Interceptor.Enqueue(new DbUpdateException("Simulated database failure."));

        using var response = await PostAsync(factory, """{"originalUrl":"https://example.com/a"}""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private static Task<HttpResponseMessage> PostAsync(ApiFactory factory, string json)
    {
        var client = factory.CreateClient();
        return client.PostAsync(ShortenEndpoint.Route, new StringContent(json, Encoding.UTF8, "application/json"));
    }
}
