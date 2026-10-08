using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Persistence;

namespace UrlShortener.UnitTests.Persistence;

// SqlException has no public constructor, so the error-number/message rule is tested
// directly. Whether real SQL Server errors carry the index name in the message is
// covered by the integration test plan (docs/integration-test-plan.md), not here.
public class SqlServerShortCodeConflictDetectorTests
{
    private const string DuplicateKeyMessage =
        "Cannot insert duplicate key row in object 'dbo.UrlMappings' with unique index " +
        "'UX_UrlMappings_ShortCode'. The duplicate key value is (aB3xY9Q).";

    [Theory]
    [InlineData(2601)]
    [InlineData(2627)]
    public void IsShortCodeConflict_True_ForUniqueViolationOnShortCodeIndex(int errorNumber)
    {
        Assert.True(SqlServerShortCodeConflictDetector.IsShortCodeConflict(errorNumber, DuplicateKeyMessage));
    }

    [Fact]
    public void IsShortCodeConflict_False_ForUniqueViolationOnAnotherIndex()
    {
        const string message =
            "Cannot insert duplicate key row in object 'dbo.UrlMappings' with unique index 'UX_Other'.";

        Assert.False(SqlServerShortCodeConflictDetector.IsShortCodeConflict(2601, message));
    }

    [Theory]
    [InlineData(547)]   // foreign key / check constraint
    [InlineData(1205)]  // deadlock victim
    [InlineData(8152)]  // string or binary data would be truncated
    public void IsShortCodeConflict_False_ForOtherSqlErrors(int errorNumber)
    {
        Assert.False(SqlServerShortCodeConflictDetector.IsShortCodeConflict(errorNumber, DuplicateKeyMessage));
    }

    [Fact]
    public void IsShortCodeConflict_False_WhenInnerExceptionIsNotSqlException()
    {
        var detector = new SqlServerShortCodeConflictDetector();
        var exception = new DbUpdateException("failed", new InvalidOperationException(DuplicateKeyMessage));

        Assert.False(detector.IsShortCodeConflict(exception));
        Assert.False(detector.IsShortCodeConflict(new DbUpdateException("failed")));
    }
}
