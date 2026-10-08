using Microsoft.AspNetCore.Http.HttpResults;
using UrlShortener.Api.Persistence.Entities;

namespace UrlShortener.Api.Shortening;

public static class ShortenEndpoint
{
    public const string Route = "/api/v1/shorten";

    public static IEndpointRouteBuilder MapShortenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, HandleAsync)
            .WithName("ShortenUrl")
            .WithOpenApi();

        return app;
    }

    public static async Task<Results<Created<ShortenUrlResponse>, Ok<ShortenUrlResponse>, ValidationProblem, ProblemHttpResult>>
        HandleAsync(
            ShortenUrlRequest request,
            UrlShorteningService service,
            ShortUrlBuilder shortUrlBuilder,
            HttpRequest httpRequest,
            CancellationToken cancellationToken)
    {
        var outcome = await service.ShortenAsync(request, cancellationToken);

        switch (outcome)
        {
            case ShortenOutcome.Created created:
            {
                var response = ToResponse(created.Mapping, shortUrlBuilder, httpRequest);
                return TypedResults.Created(response.ShortUrl, response);
            }
            case ShortenOutcome.Reused reused:
                return TypedResults.Ok(ToResponse(reused.Mapping, shortUrlBuilder, httpRequest));
            case ShortenOutcome.Invalid invalid:
                return TypedResults.ValidationProblem(invalid.Errors);
            case ShortenOutcome.ShortCodeUnavailable:
                return TypedResults.Problem(
                    title: "Could not allocate a short code.",
                    detail: "Please retry the request.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            default:
                throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}.");
        }
    }

    private static ShortenUrlResponse ToResponse(UrlMapping mapping, ShortUrlBuilder builder, HttpRequest request) =>
        new(
            mapping.ShortCode,
            builder.Build(request, mapping.ShortCode),
            mapping.OriginalUrl,
            mapping.ExpiresAtUtc);
}
