using System.Text.Json.Serialization;

namespace UrlShortener.Api.Shortening;

/// <param name="OriginalUrl">Absolute http or https URL, at most 2048 characters.</param>
/// <param name="ExpiresAtUtc">
/// Optional expiration as an ISO 8601 timestamp that must end in "Z" or a "+hh:mm"/"-hh:mm"
/// offset (enforced by <see cref="ExplicitOffsetDateTimeOffsetConverter"/>; a missing offset
/// or malformed value is rejected with 400 before validation runs). Must lie in the future;
/// converted to UTC and truncated to millisecond precision.
/// </param>
public sealed record ShortenUrlRequest(
    string? OriginalUrl,
    [property: JsonConverter(typeof(ExplicitOffsetDateTimeOffsetConverter))] DateTimeOffset? ExpiresAtUtc);

public sealed record ShortenUrlResponse(
    string ShortCode,
    string ShortUrl,
    string OriginalUrl,
    DateTime? ExpiresAtUtc);
