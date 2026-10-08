using Microsoft.AspNetCore.Http.HttpResults;
using UrlShortener.Api.Analytics;

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
    /// Each 302 queues one click event; the redirect never waits for, or fails because of, it.
    /// </summary>
    public static async Task<Results<RedirectHttpResult, NotFound>> HandleAsync(
        string code,
        ShortCodeResolver resolver,
        ClickTracker clickTracker,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var resolved = await resolver.ResolveAsync(code, cancellationToken);
        if (resolved is null)
        {
            return TypedResults.NotFound();
        }

        clickTracker.Track(resolved.UrlMappingId, httpContext);

        return TypedResults.Redirect(resolved.OriginalUrl, permanent: false, preserveMethod: false);
    }
}
