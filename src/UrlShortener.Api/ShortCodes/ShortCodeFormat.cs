namespace UrlShortener.Api.ShortCodes;

public static class ShortCodeFormat
{
    public const int Length = 7;

    public const string Alphabet =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static bool IsValid(ReadOnlySpan<char> code) =>
        code.Length == Length && code.IndexOfAnyExcept(Alphabet) < 0;
}
