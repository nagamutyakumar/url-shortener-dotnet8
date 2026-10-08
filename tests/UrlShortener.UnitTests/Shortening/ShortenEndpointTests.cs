using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Shortening;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Shortening;

// Exercises the handler's outcome-to-HTTP mapping directly, without hosting the app.
public sealed class ShortenEndpointTests : IDisposable
{
    private readonly FixedTimeProvider _timeProvider = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly FailingSaveChangesInterceptor _interceptor = new();
    private readonly UrlShortenerDbContext _dbContext;
    private readonly UrlShorteningService _service;
    private readonly ShortUrlBuilder _shortUrlBuilder =
        new(Options.Create(new ShortUrlOptions { PublicBaseUrl = "https://sho.rt" }));
    private readonly HttpRequest _httpRequest = new DefaultHttpContext().Request;

    public ShortenEndpointTests()
    {
        var options = new DbContextOptionsBuilder<UrlShortenerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(_interceptor, new InMemoryRowVersionInterceptor())
            .Options;
        _dbContext = new UrlShortenerDbContext(options, _timeProvider);
        _service = new UrlShorteningService(
            _dbContext,
            new SequenceShortCodeGenerator("First01", "Second2", "Third03", "Fourth4", "Fifth05"),
            new FakeShortCodeConflictDetector(),
            _timeProvider,
            NullLogger<UrlShorteningService>.Instance);
    }

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public async Task NewUrl_Returns201WithLocationAndBody()
    {
        var expires = new DateTimeOffset(2026, 12, 1, 2, 0, 0, TimeSpan.FromHours(2));

        var result = await HandleAsync(new ShortenUrlRequest("https://Example.com/a", expires));

        var created = Assert.IsType<Created<ShortenUrlResponse>>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        Assert.Equal("https://sho.rt/First01", created.Location);
        Assert.Equal(
            new ShortenUrlResponse("First01", "https://sho.rt/First01", "https://example.com/a", expires.UtcDateTime),
            created.Value);
        Assert.Equal(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), created.Value!.ExpiresAtUtc);
    }

    [Fact]
    public async Task EquivalentUrl_Returns200WithExistingMapping()
    {
        await HandleAsync(new ShortenUrlRequest("https://example.com/a", null));

        var result = await HandleAsync(new ShortenUrlRequest("https://EXAMPLE.com:443/a", null));

        var ok = Assert.IsType<Ok<ShortenUrlResponse>>(result.Result);
        Assert.Equal(
            new ShortenUrlResponse("First01", "https://sho.rt/First01", "https://example.com/a", null),
            ok.Value);
    }

    [Fact]
    public async Task InvalidRequest_Returns400ValidationProblem()
    {
        var result = await HandleAsync(new ShortenUrlRequest("javascript:alert(1)", null));

        var problem = Assert.IsType<ValidationProblem>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Contains(nameof(ShortenUrlRequest.OriginalUrl), problem.ProblemDetails.Errors.Keys);
    }

    [Fact]
    public async Task ExhaustedShortCodeRetries_Returns503Problem()
    {
        _interceptor.FailForever = true;

        var result = await HandleAsync(new ShortenUrlRequest("https://example.com/a", null));

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
    }

    private Task<Results<Created<ShortenUrlResponse>, Ok<ShortenUrlResponse>, ValidationProblem, ProblemHttpResult>>
        HandleAsync(ShortenUrlRequest request) =>
        ShortenEndpoint.HandleAsync(request, _service, _shortUrlBuilder, _httpRequest, CancellationToken.None);
}
