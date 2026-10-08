using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace UrlShortener.Api.Persistence;

public sealed class SqlServerShortCodeConflictDetector : IShortCodeConflictDetector
{
    public const string ShortCodeIndexName = "UX_UrlMappings_ShortCode";

    // 2601: duplicate key in a unique index. 2627: unique/primary key constraint violation.
    private const int DuplicateKeyInUniqueIndex = 2601;
    private const int UniqueConstraintViolation = 2627;

    public bool IsShortCodeConflict(DbUpdateException exception) =>
        exception.InnerException is SqlException sqlException
        && IsShortCodeConflict(sqlException.Number, sqlException.Message);

    /// <summary>
    /// The index name is checked as well as the error number so that any future
    /// unique index on this table is not mistaken for a retryable code collision.
    /// </summary>
    public static bool IsShortCodeConflict(int sqlErrorNumber, string message) =>
        sqlErrorNumber is DuplicateKeyInUniqueIndex or UniqueConstraintViolation
        && message.Contains(ShortCodeIndexName, StringComparison.Ordinal);
}
