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

/// <summary>Body of <c>PUT /api/v1/shorten/{code}</c>.</summary>
/// <remarks>
/// <see cref="OriginalUrl"/> is required and replaces the destination. <see cref="ExpiresAtUtc"/>
/// has three states: omitted keeps the current expiration, <c>null</c> clears it (the link never
/// expires), and a timestamp replaces it under the same rules as <see cref="ShortenUrlRequest"/>.
/// System.Text.Json only calls a setter for properties present in the JSON, which is how
/// "omitted" is told apart from "null".
/// </remarks>
public sealed class UpdateShortUrlRequest
{
    private readonly DateTimeOffset? _expiresAtUtc;

    /// <summary>Absolute http or https URL, at most 2048 characters.</summary>
    public string? OriginalUrl { get; init; }

    [JsonConverter(typeof(ExplicitOffsetDateTimeOffsetConverter))]
    public DateTimeOffset? ExpiresAtUtc
    {
        get => _expiresAtUtc;
        init
        {
            _expiresAtUtc = value;
            HasExpiresAtUtc = true;
        }
    }

    /// <summary>True when the JSON body contained <c>expiresAtUtc</c>, even as <c>null</c>.</summary>
    [JsonIgnore]
    public bool HasExpiresAtUtc { get; private init; }
}

public sealed record ShortenUrlResponse(
    string ShortCode,
    string ShortUrl,
    string OriginalUrl,
    DateTime? ExpiresAtUtc);
