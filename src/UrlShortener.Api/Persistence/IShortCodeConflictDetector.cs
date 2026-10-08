using Microsoft.EntityFrameworkCore;

namespace UrlShortener.Api.Persistence;

public interface IShortCodeConflictDetector
{
    /// <summary>
    /// True only when the failure was a violation of the unique short-code index,
    /// which callers may safely retry with a new code.
    /// </summary>
    bool IsShortCodeConflict(DbUpdateException exception);
}
