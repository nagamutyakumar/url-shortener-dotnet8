namespace UrlShortener.Api.Shortening;

public sealed class ShortUrlOptions
{
    public const string SectionName = "ShortUrls";

    /// <summary>
    /// Public origin short URLs are built on, e.g. "https://sho.rt". Should be set in
    /// production. When unset, the request's own scheme and Host header are used; those
    /// are only as trustworthy as the AllowedHosts filter, and X-Forwarded-* headers are
    /// deliberately not honoured.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    public static bool IsValidPublicBaseUrl(string? value) =>
        value is null
        || (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.UserInfo.Length == 0
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0);
}
