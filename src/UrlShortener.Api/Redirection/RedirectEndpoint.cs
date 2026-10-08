using Microsoft.AspNetCore.Http.HttpResults;

namespace UrlShortener.Api.Redirection;

public static class RedirectEndpoint
{
    public const string Route = "/{code}";

    public static IEndpointRouteBuilder MapRedirectEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, HandleAsync)
            .WithName("RedirectToOriginalUrl")
            .WithOpenApi();

        return app;
    }

    /// <summary>
    /// 302 to the original URL, or an empty 404 (rendered as generic problem details by
    /// UseStatusCodePages) for malformed, unknown, inactive and expired codes alike.
    /// </summary>
    public static async Task<Results<RedirectHttpResult, NotFound>> HandleAsync(
        string code,
        ShortCodeResolver resolver,
        CancellationToken cancellationToken)
    {
        var originalUrl = await resolver.ResolveAsync(code, cancellationToken);

        return originalUrl is null
            ? TypedResults.NotFound()
            : TypedResults.Redirect(originalUrl, permanent: false, preserveMethod: false);
    }
}
