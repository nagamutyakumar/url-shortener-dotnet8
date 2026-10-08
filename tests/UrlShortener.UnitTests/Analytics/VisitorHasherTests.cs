using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Analytics;

namespace UrlShortener.UnitTests.Analytics;

public sealed class VisitorHasherTests
{
    private const string Secret = "test-secret-0123456789-0123456789-abcdef";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0) Firefox/130.0";
    private static readonly IPAddress Ip = IPAddress.Parse("203.0.113.7");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithoutSecret_IsDisabledAndReturnsNull(string? secret)
    {
        var hasher = CreateHasher(secret);

        Assert.False(hasher.IsEnabled);
        Assert.Null(hasher.Compute(1, Ip, UserAgent));
    }

    [Fact]
    public void WithoutRemoteIp_ReturnsNull()
    {
        Assert.Null(CreateHasher(Secret).Compute(1, null, UserAgent));
    }

    [Fact]
    public void IsKeyedHmacOfMappingIpAndUserAgent()
    {
        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret),
            Encoding.UTF8.GetBytes($"42\n203.0.113.7\n{UserAgent}"));

        var hash = CreateHasher(Secret).Compute(42, Ip, UserAgent);

        Assert.Equal(expected, hash);
        Assert.Equal(32, hash!.Length);
    }

    [Fact]
    public void SameInputs_SameHash()
    {
        var hasher = CreateHasher(Secret);

        Assert.Equal(hasher.Compute(1, Ip, UserAgent), hasher.Compute(1, IPAddress.Parse("203.0.113.7"), UserAgent));
    }

    [Fact]
    public void DiffersByMappingIpUserAgentAndSecret()
    {
        var hasher = CreateHasher(Secret);
        var baseline = hasher.Compute(1, Ip, UserAgent);

        Assert.NotEqual(baseline, hasher.Compute(2, Ip, UserAgent));
        Assert.NotEqual(baseline, hasher.Compute(1, IPAddress.Parse("203.0.113.8"), UserAgent));
        Assert.NotEqual(baseline, hasher.Compute(1, Ip, UserAgent + " "));
        Assert.NotEqual(baseline, CreateHasher(Secret + "x").Compute(1, Ip, UserAgent));
    }

    [Fact]
    public void Ipv4MappedIpv6_HashesLikeIpv4()
    {
        var hasher = CreateHasher(Secret);

        Assert.Equal(hasher.Compute(1, Ip, UserAgent), hasher.Compute(1, Ip.MapToIPv6(), UserAgent));
    }

    [Fact]
    public void MissingUserAgent_StillHashesWithIp()
    {
        var hasher = CreateHasher(Secret);

        Assert.Equal(hasher.Compute(1, Ip, null), hasher.Compute(1, Ip, ""));
        Assert.NotNull(hasher.Compute(1, Ip, null));
    }

    private static VisitorHasher CreateHasher(string? secret) =>
        new(Options.Create(new ClickAnalyticsOptions { VisitorHashSecret = secret }));
}
