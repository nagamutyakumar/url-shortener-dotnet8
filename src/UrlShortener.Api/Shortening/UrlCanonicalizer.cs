using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using UrlShortener.Api.Persistence.Configurations;

namespace UrlShortener.Api.Shortening;

/// <summary>
/// Validates a user-supplied URL and produces the canonical form that is stored
/// and used for duplicate detection. Purely syntactic: never resolves or fetches.
/// </summary>
/// <remarks>
/// Only transformations that RFC 3986 (section 6.2.2/6.2.3) defines as equivalence-preserving
/// for http/https are applied, all of them via <see cref="Uri"/>:
/// lowercase scheme and host, IDN host to punycode, default port removed, empty path
/// becomes "/", dot segments resolved, percent-encoded unreserved characters decoded,
/// and characters that must be escaped are percent-encoded.
/// Path, query and fragment are otherwise kept byte-for-byte: no case folding, no
/// trailing-slash changes, no query reordering and no tracking-parameter removal,
/// because servers are free to treat those differently.
/// </remarks>
public static class UrlCanonicalizer
{
    public const int MaxLength = UrlMappingConfiguration.OriginalUrlMaxLength;

    public static bool TryCanonicalize(
        string? input,
        [NotNullWhen(true)] out string? canonicalUrl,
        [NotNullWhen(false)] out string? error)
    {
        canonicalUrl = null;
        var candidate = input?.Trim();

        if (string.IsNullOrEmpty(candidate))
        {
            error = "A URL is required.";
            return false;
        }

        if (candidate.Length > MaxLength)
        {
            error = $"The URL must be at most {MaxLength} characters.";
            return false;
        }

        // Uri would silently escape these; rejecting is more predictable and keeps CR/LF out.
        if (candidate.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            error = "The URL must not contain whitespace or control characters; percent-encode them.";
            return false;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            error = "The URL must be a valid absolute URL.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Only http and https URLs are supported.";
            return false;
        }

        if (uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            error = "The URL must have a valid host.";
            return false;
        }

        // Credentials in a shared link leak secrets and enable "trusted.com@evil.com" spoofing.
        if (uri.UserInfo.Length > 0)
        {
            error = "URLs containing user credentials are not supported.";
            return false;
        }

        // Uri.Host keeps the brackets an IPv6 literal needs; IdnHost drops them.
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        var authority = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        var canonical = $"{uri.Scheme}://{authority}{uri.PathAndQuery}{uri.Fragment}";

        // Escaping and punycode can lengthen the URL past the column limit.
        if (canonical.Length > MaxLength)
        {
            error = $"The URL must be at most {MaxLength} characters after normalization.";
            return false;
        }

        canonicalUrl = canonical;
        error = null;
        return true;
    }

    /// <summary>SHA-256 of the UTF-8 canonical URL, stored as <c>OriginalUrlHash</c>.</summary>
    public static byte[] ComputeHash(string canonicalUrl) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(canonicalUrl));
}
