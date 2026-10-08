using UrlShortener.Api.ShortCodes;

namespace UrlShortener.UnitTests.ShortCodes;

public class ShortCodeFormatTests
{
    [Fact]
    public void Length_IsSeven()
    {
        Assert.Equal(7, ShortCodeFormat.Length);
    }

    [Fact]
    public void Alphabet_IsExactBase62Set()
    {
        Assert.Equal(
            "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz",
            ShortCodeFormat.Alphabet);
    }

    [Theory]
    [InlineData("0000000")]
    [InlineData("zzzzzzz")]
    [InlineData("aB3xY9Q")]
    [InlineData("Zz09Aa1")]
    public void IsValid_ReturnsTrue_ForSevenBase62Characters(string code)
    {
        Assert.True(ShortCodeFormat.IsValid(code));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc123")]
    [InlineData("abc12345")]
    [InlineData("abc-123")]
    [InlineData("abc_123")]
    [InlineData("abc 123")]
    [InlineData("abc!123")]
    [InlineData("abc/123")]
    [InlineData("abcé123")]
    public void IsValid_ReturnsFalse_ForWrongLengthOrDisallowedCharacters(string code)
    {
        Assert.False(ShortCodeFormat.IsValid(code));
    }
}
