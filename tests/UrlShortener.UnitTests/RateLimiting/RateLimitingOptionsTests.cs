using System.Net;
using UrlShortener.Api.RateLimiting;

namespace UrlShortener.UnitTests.RateLimiting;

public sealed class RateLimitingOptionsTests
{
    [Fact]
    public void Defaults_Are20MutationsAnd60AnalyticsPerMinuteWithoutQueueing()
    {
        var options = new RateLimitingOptions();

        Assert.True(RateLimitingOptions.IsValid(options));
        Assert.Equal(20, options.Mutations.PermitLimit);
        Assert.Equal(60, options.Analytics.PermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Mutations.Window);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Analytics.Window);
        Assert.Equal(0, options.Mutations.QueueLimit);
        Assert.Equal(0, options.Analytics.QueueLimit);
    }

    [Theory]
    [InlineData(1, 1, 0, true)]
    [InlineData(100_000, 3600, 1_000, true)]
    [InlineData(0, 60, 0, false)]
    [InlineData(100_001, 60, 0, false)]
    [InlineData(20, 0, 0, false)]
    [InlineData(20, 3601, 0, false)]
    [InlineData(20, 60, -1, false)]
    [InlineData(20, 60, 1_001, false)]
    public void IsValid_ChecksEachBound(int permitLimit, int windowSeconds, int queueLimit, bool expected)
    {
        var policy = new FixedWindowPolicyOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = queueLimit,
        };

        Assert.Equal(expected, RateLimitingOptions.IsValid(new RateLimitingOptions { Mutations = policy }));
        Assert.Equal(expected, RateLimitingOptions.IsValid(new RateLimitingOptions { Analytics = policy }));
    }

    [Theory]
    [InlineData("203.0.113.10", "203.0.113.10")]
    [InlineData("::ffff:203.0.113.10", "203.0.113.10")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("::1", "::1")]
    public void GetPartitionKey_NormalizesIpv4MappedIpv6(string address, string expected) =>
        Assert.Equal(expected, RateLimitingServiceCollectionExtensions.GetPartitionKey(IPAddress.Parse(address)));

    [Fact]
    public void GetPartitionKey_NoAddress_UsesFallbackPartition() =>
        Assert.Equal(
            RateLimitingServiceCollectionExtensions.UnknownClientPartition,
            RateLimitingServiceCollectionExtensions.GetPartitionKey(null));
}
