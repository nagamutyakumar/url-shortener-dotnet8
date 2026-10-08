using System.Text.Json;
using UrlShortener.Api.Shortening;

namespace UrlShortener.UnitTests.Shortening;

// Deserializes the request record the way minimal APIs bind a JSON body (web defaults),
// so the [JsonConverter] attribute on ExpiresAtUtc is exercised as in production.
public sealed class ExplicitOffsetDateTimeOffsetConverterTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("2026-10-09T12:00:00Z", 0)]
    [InlineData("2026-10-09T17:30:00+05:30", 330)]
    [InlineData("2026-10-09T04:00:00-08:00", -480)]
    [InlineData("2026-10-09T12:00:00+00:00", 0)]
    public void Reads_TimestampWithUtcDesignatorOrOffset(string json, int offsetMinutes)
    {
        var request = Deserialize(json);

        Assert.Equal(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), request.ExpiresAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(offsetMinutes), request.ExpiresAtUtc!.Value.Offset);
    }

    [Fact]
    public void Reads_SubmillisecondPrecisionUnchanged()
    {
        var request = Deserialize("2026-10-09T12:00:00.1234567Z");

        // Truncation to milliseconds is the service's job, not the converter's.
        Assert.Equal(
            new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567),
            request.ExpiresAtUtc);
    }

    [Fact]
    public void Reads_NullAndMissingAsNoExpiration()
    {
        Assert.Null(JsonSerializer.Deserialize<ShortenUrlRequest>(
            """{"originalUrl":"https://example.com/","expiresAtUtc":null}""", WebOptions)!.ExpiresAtUtc);
        Assert.Null(JsonSerializer.Deserialize<ShortenUrlRequest>(
            """{"originalUrl":"https://example.com/"}""", WebOptions)!.ExpiresAtUtc);
    }

    [Theory]
    [InlineData("2026-10-09T12:00:00")]
    [InlineData("2026-10-09T12:00:00.123")]
    [InlineData("2026-10-09T12:00")]
    [InlineData("2026-10-09")]
    public void Rejects_TimestampWithoutTimezone(string json)
    {
        Assert.Throws<JsonException>(() => Deserialize(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("tomorrow")]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("2026-10-09T25:00:00Z")]
    [InlineData("2026-10-09 12:00:00Z")]
    [InlineData("2026-10-09T12:00:00+0530")]
    [InlineData("2026-10-09T12:00:00+05")]
    [InlineData("2026-10-09T12:00:00Z ")]
    [InlineData("10/09/2026 12:00:00 +00:00")]
    public void Rejects_MalformedTimestamp(string json)
    {
        Assert.Throws<JsonException>(() => Deserialize(json));
    }

    [Theory]
    [InlineData("1791633600")]
    [InlineData("true")]
    [InlineData("{}")]
    public void Rejects_NonStringToken(string rawJson)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ShortenUrlRequest>(
            $$"""{"originalUrl":"https://example.com/","expiresAtUtc":{{rawJson}}}""", WebOptions));
    }

    private static ShortenUrlRequest Deserialize(string expiresAtUtc) =>
        JsonSerializer.Deserialize<ShortenUrlRequest>(
            $$"""{"originalUrl":"https://example.com/","expiresAtUtc":"{{expiresAtUtc}}"}""",
            WebOptions)!;
}
