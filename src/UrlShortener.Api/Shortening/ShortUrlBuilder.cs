using Microsoft.Extensions.Options;

namespace UrlShortener.Api.Shortening;

public sealed class ShortUrlBuilder
{
    private readonly string? _publicBaseUrl;

    public ShortUrlBuilder(IOptions<ShortUrlOptions> options)
    {
        _publicBaseUrl = options.Value.PublicBaseUrl?.TrimEnd('/');
    }

    public string Build(HttpRequest request, string shortCode)
    {
        // Scheme and Host come from the connection and the Host header as seen by Kestrel;
        // forwarded headers are not applied because no ForwardedHeaders middleware is configured.
        var baseUrl = _publicBaseUrl
            ?? $"{request.Scheme}://{request.Host.ToUriComponent()}{request.PathBase.ToUriComponent()}";

        return $"{baseUrl}/{shortCode}";
    }
}
