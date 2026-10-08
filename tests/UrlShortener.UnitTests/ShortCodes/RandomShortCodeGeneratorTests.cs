using UrlShortener.Api.ShortCodes;

namespace UrlShortener.UnitTests.ShortCodes;

public class RandomShortCodeGeneratorTests
{
    private const int SampleSize = 1_000;

    private readonly RandomShortCodeGenerator _generator = new();

    [Fact]
    public void Generate_ReturnsCodeOfConfiguredLength()
    {
        for (var i = 0; i < SampleSize; i++)
        {
            Assert.Equal(ShortCodeFormat.Length, _generator.Generate().Length);
        }
    }

    [Fact]
    public void Generate_ReturnsOnlyAlphabetCharacters()
    {
        for (var i = 0; i < SampleSize; i++)
        {
            var code = _generator.Generate();
            Assert.All(code, c => Assert.Contains(c, ShortCodeFormat.Alphabet));
        }
    }

    [Fact]
    public void Generate_ReturnsCodesThatPassFormatValidation()
    {
        for (var i = 0; i < SampleSize; i++)
        {
            Assert.True(ShortCodeFormat.IsValid(_generator.Generate()));
        }
    }
}
