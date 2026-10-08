using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Shortening;

namespace UrlShortener.UnitTests.Shortening;

public class ShortUrlBuilderTests
{
    [Theory]
    [InlineData("https://sho.rt", "https://sho.rt/Abc1234")]
    [InlineData("https://sho.rt/", "https://sho.rt/Abc1234")]
    [InlineData("https://example.com/s", "https://example.com/s/Abc1234")]
    public void Build_UsesConfiguredPublicBaseUrl_OverRequestHost(string baseUrl, string expected)
    {
        var builder = CreateBuilder(baseUrl);

        Assert.Equal(expected, builder.Build(CreateRequest("http", "internal-host:5000"), "Abc1234"));
    }

    [Fact]
    public void Build_FallsBackToRequestSchemeHostAndPathBase()
    {
        var builder = CreateBuilder(null);
        var request = CreateRequest("https", "localhost:7215");
        request.PathBase = "/app";

        Assert.Equal("https://localhost:7215/app/Abc1234", builder.Build(request, "Abc1234"));
    }

    [Fact]
    public void Build_IgnoresForwardedHeaders()
    {
        var builder = CreateBuilder(null);
        var request = CreateRequest("http", "localhost:5210");
        request.Headers["X-Forwarded-Host"] = "evil.example";
        request.Headers["X-Forwarded-Proto"] = "https";

        Assert.Equal("http://localhost:5210/Abc1234", builder.Build(request, "Abc1234"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("https://sho.rt", true)]
    [InlineData("http://localhost:5210/s/", true)]
    [InlineData("sho.rt", false)]
    [InlineData("ftp://sho.rt", false)]
    [InlineData("https://user:pw@sho.rt", false)]
    [InlineData("https://sho.rt/?a=1", false)]
    [InlineData("https://sho.rt/#x", false)]
    public void IsValidPublicBaseUrl_AcceptsOnlyPlainHttpOrigins(string? value, bool expected)
    {
        Assert.Equal(expected, ShortUrlOptions.IsValidPublicBaseUrl(value));
    }

    private static ShortUrlBuilder CreateBuilder(string? publicBaseUrl) =>
        new(Options.Create(new ShortUrlOptions { PublicBaseUrl = publicBaseUrl }));

    private static HttpRequest CreateRequest(string scheme, string host)
    {
        var request = new DefaultHttpContext().Request;
        request.Scheme = scheme;
        request.Host = new HostString(host);
        return request;
    }
}
