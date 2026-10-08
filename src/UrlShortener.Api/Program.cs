using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.ShortCodes;

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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.Run();