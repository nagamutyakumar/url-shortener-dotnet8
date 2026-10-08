using System.Security.Cryptography;

namespace UrlShortener.Api.ShortCodes;

public sealed class RandomShortCodeGenerator : IShortCodeGenerator
{
    // RandomNumberGenerator.GetString uses the OS CSPRNG and selects each
    // character via rejection sampling, so every character is equally likely.
    public string Generate() =>
        RandomNumberGenerator.GetString(ShortCodeFormat.Alphabet, ShortCodeFormat.Length);
}
