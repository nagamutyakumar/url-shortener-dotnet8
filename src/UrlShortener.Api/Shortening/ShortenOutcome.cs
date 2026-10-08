using UrlShortener.Api.Persistence.Entities;

namespace UrlShortener.Api.Shortening;

public abstract record ShortenOutcome
{
    private ShortenOutcome()
    {
    }

    public sealed record Created(UrlMapping Mapping) : ShortenOutcome;

    public sealed record Reused(UrlMapping Mapping) : ShortenOutcome;

    public sealed record Invalid(IDictionary<string, string[]> Errors) : ShortenOutcome;

    /// <summary>Every attempt hit a short-code collision; retrying the request may succeed.</summary>
    public sealed record ShortCodeUnavailable : ShortenOutcome;
}
