using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Redirection;
using UrlShortener.Api.ShortCodes;
using UrlShortener.Api.Shortening;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("UrlShortener")
    ?? throw new InvalidOperationException(
        "Connection string 'UrlShortener' is not configured. Set ConnectionStrings:UrlShortener " +
        "via user-secrets or the ConnectionStrings__UrlShortener environment variable.");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IShortCodeGenerator, RandomShortCodeGenerator>();
builder.Services.AddDbContext<UrlShortenerDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddSingleton<IShortCodeConflictDetector, SqlServerShortCodeConflictDetector>();
builder.Services.AddScoped<UrlShorteningService>();
builder.Services.AddScoped<ShortCodeResolver>();
builder.Services.AddSingleton<ShortUrlBuilder>();
builder.Services.AddOptions<ShortUrlOptions>()
    .Bind(builder.Configuration.GetSection(ShortUrlOptions.SectionName))
    .Validate(
        o => ShortUrlOptions.IsValidPublicBaseUrl(o.PublicBaseUrl),
        $"{ShortUrlOptions.SectionName}:PublicBaseUrl must be an absolute http(s) URL without credentials, query or fragment.")
    .ValidateOnStart();
builder.Services.AddOptions<ClickAnalyticsOptions>()
    .Bind(builder.Configuration.GetSection(ClickAnalyticsOptions.SectionName))
    .Validate(
        ClickAnalyticsOptions.IsValid,
        $"{ClickAnalyticsOptions.SectionName}: QueueCapacity must be 1-1000000, BatchSize 1-1000, and " +
        $"VisitorHashSecret, when set, at least {ClickAnalyticsOptions.MinVisitorHashSecretLength} characters.")
    .ValidateOnStart();
// Click analytics: redirects enqueue into a bounded in-memory channel; a hosted service
// persists the events. Best-effort by design (see ClickEventQueue and ClickEventWriter).
builder.Services.AddSingleton<ClickEventQueue>();
builder.Services.AddSingleton<IClickEventQueue>(sp => sp.GetRequiredService<ClickEventQueue>());
builder.Services.AddSingleton<VisitorHasher>();
builder.Services.AddSingleton<ClickTracker>();
builder.Services.AddHostedService<ClickEventWriter>();
builder.Services.AddScoped<ClickAnalyticsService>();
builder.Services.AddProblemDetails();
// Development defaults this to true, which turns unreadable request bodies into
// BadHttpRequestException; UseExceptionHandler (.NET 8) would then answer 500 instead of 400.
// With false, binding failures return 400 in every environment and are logged at Debug.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapShortenEndpoint();
app.MapClickAnalyticsEndpoint();
app.MapRedirectEndpoint();

app.Run();

// Exposes the entry point to WebApplicationFactory<Program> in the HTTP-level tests.
public partial class Program;
