using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Persistence;
using UrlShortener.UnitTests.TestDoubles;

namespace UrlShortener.UnitTests.Analytics;

// Translates the analytics queries with the real SQL Server provider (no connection is opened)
// to prove the aggregation runs in SQL rather than failing or pulling rows to the client.
// It does not execute the SQL; see docs/integration-test-plan.md for what that leaves unproven.
public sealed class ClickAnalyticsSqlTranslationTests
{
    [Fact]
    public void SummaryQuery_IsSingleServerSideAggregate()
    {
        using var context = CreateSqlServerContext();

        var sql = ClickAnalyticsService.SummaryQuery(context, 42).ToQueryString();

        Assert.Contains("COUNT_BIG(*)", sql);
        Assert.Contains("COUNT_BIG(DISTINCT", sql);
        Assert.Contains("[VisitorHash]", sql);
        Assert.Contains("[CountryCode] IS NULL", sql);
        Assert.Contains("WHERE [c].[UrlMappingId] = @__urlMappingId_0", sql);
        Assert.Contains("GROUP BY [c].[UrlMappingId]", sql);
    }

    [Fact]
    public void CountryQuery_GroupsAndOrdersInSql()
    {
        using var context = CreateSqlServerContext();

        var sql = ClickAnalyticsService.CountryQuery(context, 42).ToQueryString();

        Assert.Contains("[CountryCode] IS NOT NULL", sql);
        Assert.Contains("GROUP BY [c].[CountryCode]", sql);
        Assert.Contains("ORDER BY COUNT_BIG(*) DESC, [c].[CountryCode]", sql);
    }

    private static UrlShortenerDbContext CreateSqlServerContext() => new(
        new DbContextOptionsBuilder<UrlShortenerDbContext>()
            .UseSqlServer("Server=unused;Database=unused")
            .Options,
        new FixedTimeProvider(DateTimeOffset.UnixEpoch));
}
