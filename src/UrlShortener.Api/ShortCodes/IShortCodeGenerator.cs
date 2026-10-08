namespace UrlShortener.Api.ShortCodes;

public interface IShortCodeGenerator
{
    /// <summary>
    /// Returns a new random candidate short code. Uniqueness is not guaranteed;
    /// callers are responsible for enforcing it.
    /// </summary>
    string Generate();
}
