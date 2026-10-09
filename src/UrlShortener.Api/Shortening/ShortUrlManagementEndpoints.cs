using Microsoft.AspNetCore.Http.HttpResults;
using UrlShortener.Api.RateLimiting;

namespace UrlShortener.Api.Shortening;

/// <summary>
/// PUT and DELETE for an existing short code. Unauthenticated: anyone who knows a code can
/// change or delete it, which is acceptable only for a controlled assessment or demo.
/// </summary>
public static class ShortUrlManagementEndpoints
{
    public const string Route = "/api/v1/shorten/{code}";

    public static IEndpointRouteBuilder MapShortUrlManagementEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut(Route, UpdateAsync)
            .WithName("UpdateShortUrl")
            .WithOpenApi()
            .RequireRateLimiting(RateLimitingServiceCollectionExtensions.MutationsPolicy);

        app.MapDelete(Route, DeleteAsync)
            .WithName("DeleteShortUrl")
            .WithOpenApi()
            .RequireRateLimiting(RateLimitingServiceCollectionExtensions.MutationsPolicy);

        return app;
    }

    /// <summary>
    /// 200 with the updated mapping; 400 for an invalid body; an empty 404 for malformed, unknown,
    /// wrong-case or inactive codes; 409 when the mapping changed concurrently.
    /// </summary>
    public static async Task<Results<Ok<ShortenUrlResponse>, ValidationProblem, NotFound, ProblemHttpResult>>
        UpdateAsync(
            string code,
            UpdateShortUrlRequest request,
            ShortUrlManagementService service,
            ShortUrlBuilder shortUrlBuilder,
            HttpRequest httpRequest,
            CancellationToken cancellationToken)
    {
        var outcome = await service.UpdateAsync(code, request, cancellationToken);

        switch (outcome)
        {
            case ManagementOutcome.Updated updated:
            {
                var mapping = updated.Mapping;
                return TypedResults.Ok(new ShortenUrlResponse(
                    mapping.ShortCode,
                    shortUrlBuilder.Build(httpRequest, mapping.ShortCode),
                    mapping.OriginalUrl,
                    mapping.ExpiresAtUtc));
            }
            case ManagementOutcome.Invalid invalid:
                return TypedResults.ValidationProblem(invalid.Errors);
            case ManagementOutcome.NotFound:
                return TypedResults.NotFound();
            case ManagementOutcome.Conflict:
                return ConcurrencyConflict();
            default:
                throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.");
        }
    }

    /// <summary>
    /// 204 when the mapping is now inactive, including when it already was; an empty 404 for
    /// malformed, unknown or wrong-case codes; 409 when the mapping changed concurrently.
    /// </summary>
    public static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        string code,
        ShortUrlManagementService service,
        CancellationToken cancellationToken)
    {
        var outcome = await service.DeleteAsync(code, cancellationToken);

        return outcome switch
        {
            ManagementOutcome.Deleted => TypedResults.NoContent(),
            ManagementOutcome.NotFound => TypedResults.NotFound(),
            ManagementOutcome.Conflict => ConcurrencyConflict(),
            _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}."),
        };
    }

    private static ProblemHttpResult ConcurrencyConflict() =>
        TypedResults.Problem(
            title: "The short URL was modified concurrently.",
            detail: "No changes were saved. Retry the request.",
            statusCode: StatusCodes.Status409Conflict);
}
