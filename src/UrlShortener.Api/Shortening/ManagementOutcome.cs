using UrlShortener.Api.Persistence.Entities;

namespace UrlShortener.Api.Shortening;

public abstract record ManagementOutcome
{
    private ManagementOutcome()
    {
    }

    public sealed record Updated(UrlMapping Mapping) : ManagementOutcome;

    /// <summary>The mapping is inactive, whether by this request or an earlier one.</summary>
    public sealed record Deleted : ManagementOutcome;

    /// <summary>Malformed, unknown or wrong-case code; for updates, also an inactive mapping.</summary>
    public sealed record NotFound : ManagementOutcome;

    public sealed record Invalid(IDictionary<string, string[]> Errors) : ManagementOutcome;

    /// <summary>The mapping changed between this request's read and its write; nothing was saved.</summary>
    public sealed record Conflict : ManagementOutcome;
}
