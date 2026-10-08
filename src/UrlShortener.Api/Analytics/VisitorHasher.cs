using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace UrlShortener.Api.Analytics;

/// <summary>
/// Derives a keyed, per-link pseudonymous visitor identifier so unique visitors can be
/// approximated without storing IP addresses or user agents.
/// </summary>
/// <remarks>
/// HMAC-SHA256(secret, urlMappingId | remote IP | User-Agent). Including the mapping id means
/// the same visitor gets unrelated hashes on different links. It is an approximation: visitors
/// sharing an IP and browser (NAT, corporate proxies) collapse into one, and a visitor whose
/// IP or browser changes counts again. The remote IP is the TCP peer: X-Forwarded-For is not
/// honoured, so behind a reverse proxy every visitor shares the proxy's address unless
/// ForwardedHeaders middleware with known proxies is configured. Anyone holding the secret can
/// test guesses (IPv4 space × common user agents), so it must be protected like a password.
/// </remarks>
public sealed class VisitorHasher
{
    private readonly byte[]? _key;

    public VisitorHasher(IOptions<ClickAnalyticsOptions> options)
    {
        var secret = options.Value.VisitorHashSecret;
        _key = string.IsNullOrEmpty(secret) ? null : Encoding.UTF8.GetBytes(secret);
    }

    public bool IsEnabled => _key is not null;

    /// <returns>
    /// A 32-byte hash, or null when no secret is configured or the remote IP is unknown
    /// (a user agent alone is far too coarse to identify visitors).
    /// </returns>
    public byte[]? Compute(long urlMappingId, IPAddress? remoteIp, string? userAgent)
    {
        if (_key is null || remoteIp is null)
        {
            return null;
        }

        // A dual-stack socket reports IPv4 clients as ::ffff:a.b.c.d; normalise so the same
        // client hashes identically however it connected.
        if (remoteIp.IsIPv4MappedToIPv6)
        {
            remoteIp = remoteIp.MapToIPv4();
        }

        var input = Encoding.UTF8.GetBytes($"{urlMappingId}\n{remoteIp}\n{userAgent}");
        return HMACSHA256.HashData(_key, input);
    }
}
