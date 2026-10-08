using Microsoft.AspNetCore.Http.HttpResults;

namespace UrlShortener.Api.Analytics;

public static class ClickAnalyticsEndpoint
{
    public const string Route = "/api/v1/analytics/{code}";

    public static IEndpointRouteBuilder MapClickAnalyticsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, HandleAsync)
            .WithName("GetClickAnalytics")
            .WithOpenApi();

        return app;
    }

    /// <summary>
    /// 200 with click analytics for any existing mapping, including inactive and expired ones;
    /// an empty 404 (generic problem details via UseStatusCodePages) for malformed or unknown
    /// codes, matching codes case-sensitively.
    /// </summary>
    public static async Task<Results<Ok<ClickAnalyticsResponse>, NotFound>> HandleAsync(
        string code,
        ClickAnalyticsService service,
        CancellationToken cancellationToken)
    {
        var analytics = await service.GetAsync(code, cancellationToken);

        return analytics is null ? TypedResults.NotFound() : TypedResults.Ok(analytics);
    }
}
