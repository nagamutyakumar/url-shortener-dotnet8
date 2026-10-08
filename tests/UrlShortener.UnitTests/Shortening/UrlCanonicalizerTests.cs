using UrlShortener.Api.Shortening;

namespace UrlShortener.UnitTests.Shortening;

public class UrlCanonicalizerTests
{
    [Theory]
    [InlineData("HTTP://EXAMPLE.COM", "http://example.com/")]
    [InlineData("http://example.com:80/a", "http://example.com/a")]
    [InlineData("https://example.com:443/a", "https://example.com/a")]
    [InlineData("http://example.com:8080/a", "http://example.com:8080/a")]
    [InlineData("  https://example.com/a  ", "https://example.com/a")]
    [InlineData("http://bücher.de/", "http://xn--bcher-kva.de/")]
    [InlineData("http://example.com/a/../b/./c", "http://example.com/b/c")]
    [InlineData("http://example.com/%7Euser", "http://example.com/~user")]
    [InlineData("http://[::1]:80/x", "http://[::1]/x")]
    [InlineData("http://127.0.0.1:8080/x", "http://127.0.0.1:8080/x")]
    public void TryCanonicalize_AppliesEquivalencePreservingNormalization(string input, string expected)
    {
        Assert.True(UrlCanonicalizer.TryCanonicalize(input, out var canonical, out _));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData("https://example.com/Path/To/Page")]
    [InlineData("https://example.com/a/")]
    [InlineData("https://example.com/a")]
    [InlineData("https://example.com/?b=2&a=1&utm_source=x")]
    [InlineData("https://example.com/a?Q=Value#Section")]
    [InlineData("https://example.com/a%2Fb")]
    public void TryCanonicalize_PreservesPathQueryAndFragment(string input)
    {
        Assert.True(UrlCanonicalizer.TryCanonicalize(input, out var canonical, out _));
        Assert.Equal(input, canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com")]
    [InlineData("/relative/path")]
    [InlineData("//example.com/a")]
    [InlineData("http:/example.com")]
    [InlineData("http://")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("http://user:secret@example.com/")]
    [InlineData("https://trusted.com@evil.com/")]
    [InlineData("http://example.com/a b")]
    [InlineData("http://example.com/a\r\nSet-Cookie: x=1")]
    [InlineData("http://example.com/\ta")]
    public void TryCanonicalize_RejectsInvalidOrUnsupportedUrls(string? input)
    {
        Assert.False(UrlCanonicalizer.TryCanonicalize(input, out var canonical, out var error));
        Assert.Null(canonical);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryCanonicalize_AcceptsUrlAtMaxLength()
    {
        var url = PadUrl(UrlCanonicalizer.MaxLength);

        Assert.True(UrlCanonicalizer.TryCanonicalize(url, out var canonical, out _));
        Assert.Equal(url, canonical);
    }

    [Fact]
    public void TryCanonicalize_RejectsUrlOverMaxLength()
    {
        Assert.False(UrlCanonicalizer.TryCanonicalize(PadUrl(UrlCanonicalizer.MaxLength + 1), out _, out var error));
        Assert.Contains("2048", error);
    }

    [Fact]
    public void TryCanonicalize_RejectsUrlThatExceedsMaxLengthOnlyAfterEscaping()
    {
        // Each "é" becomes "%C3%A9" (6 characters) once percent-encoded.
        var prefix = "https://example.com/";
        var url = prefix + new string('é', (UrlCanonicalizer.MaxLength - prefix.Length) / 2);
        Assert.True(url.Length <= UrlCanonicalizer.MaxLength);

        Assert.False(UrlCanonicalizer.TryCanonicalize(url, out _, out var error));
        Assert.Contains("normalization", error);
    }

    [Fact]
    public void ComputeHash_IsSha256OfCanonicalUrl()
    {
        var hash = UrlCanonicalizer.ComputeHash("https://example.com/");

        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, UrlCanonicalizer.ComputeHash("https://example.com/"));
        Assert.NotEqual(hash, UrlCanonicalizer.ComputeHash("https://example.com/A"));
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("https://example.com/"u8)),
            Convert.ToHexString(hash));
    }

    private static string PadUrl(int totalLength)
    {
        const string prefix = "https://example.com/";
        return prefix + new string('a', totalLength - prefix.Length);
    }
}
