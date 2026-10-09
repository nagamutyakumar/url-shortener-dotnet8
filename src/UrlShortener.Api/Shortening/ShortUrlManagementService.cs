using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Caching;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Persistence.Entities;
using UrlShortener.Api.ShortCodes;

namespace UrlShortener.Api.Shortening;

/// <summary>
/// Updates the destination/expiration of an existing short code, or soft-deletes it.
/// </summary>
/// <remarks>
/// <para>Codes match exactly (case-sensitive), as for redirects and analytics. Writes go through
/// the tracked entity, so EF updates by primary key and a code differing only in case can never
/// be modified.</para>
/// <para>Concurrency: the UPDATE includes the <c>RowVersion</c> read at the start of the request.
/// If another writer changed the row in between, nothing is saved and
/// <see cref="ManagementOutcome.Conflict"/> is returned; the caller decides whether to retry.
/// This guards the read-modify-write inside one request only. Across separate requests the last
/// writer wins, because no ETag/If-Match token is exchanged with clients (see docs/management.md).</para>
/// <para>The mapping's Id is never changed, so its click history stays attached.</para>
/// <para>Redirect cache: after a successful write (and on a repeated delete) the code's entry is
/// removed, after the SQL commit. A failed removal is logged and does not change the response,
/// because the write is already committed; the stale entry then lives at most
/// <see cref="RedirectCacheOptions.MaxTimeToLive"/>. See docs/caching.md.</para>
/// </remarks>
public sealed class ShortUrlManagementService
{
    private readonly UrlShortenerDbContext _dbContext;
    private readonly IRedirectCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ShortUrlManagementService> _logger;

    public ShortUrlManagementService(
        UrlShortenerDbContext dbContext,
        IRedirectCache cache,
        TimeProvider timeProvider,
        ILogger<ShortUrlManagementService> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Replaces the destination URL and, when <see cref="UpdateShortUrlRequest.HasExpiresAtUtc"/>
    /// is set, the expiration. Inactive mappings are not found, so a deleted link cannot be
    /// revived. Expired but active mappings can be updated; a new future (or null) expiration
    /// makes them redirect again, while an omitted expiration leaves them expired.
    /// </summary>
    public async Task<ManagementOutcome> UpdateAsync(
        string code,
        UpdateShortUrlRequest request,
        CancellationToken cancellationToken)
    {
        if (!ShortCodeFormat.IsValid(code))
        {
            return new ManagementOutcome.NotFound();
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var errors = new Dictionary<string, string[]>();
        if (!UrlCanonicalizer.TryCanonicalize(request.OriginalUrl, out var canonicalUrl, out var urlError))
        {
            errors[nameof(UpdateShortUrlRequest.OriginalUrl)] = new[] { urlError };
        }

        DateTime? expiresAtUtc = null;
        if (request.HasExpiresAtUtc
            && !ExpirationRules.TryNormalize(request.ExpiresAtUtc, now, out expiresAtUtc, out var expirationError))
        {
            errors[nameof(UpdateShortUrlRequest.ExpiresAtUtc)] = new[] { expirationError };
        }

        if (errors.Count > 0)
        {
            return new ManagementOutcome.Invalid(errors);
        }

        var mapping = await FindAsync(code, cancellationToken);
        if (mapping is null || !mapping.IsActive)
        {
            return new ManagementOutcome.NotFound();
        }

        mapping.OriginalUrl = canonicalUrl!;
        mapping.OriginalUrlHash = UrlCanonicalizer.ComputeHash(canonicalUrl!);
        if (request.HasExpiresAtUtc)
        {
            mapping.ExpiresAtUtc = expiresAtUtc;
        }

        // An unchanged body leaves the entity Unchanged, so nothing is written.
        if (!await TrySaveAsync(mapping, cancellationToken))
        {
            return new ManagementOutcome.Conflict();
        }

        await InvalidateCacheAsync(code);
        return new ManagementOutcome.Updated(mapping);
    }

    /// <summary>
    /// Sets <c>IsActive = false</c>; the row and its click events are kept. Deleting an already
    /// inactive mapping succeeds without writing, so a retried DELETE gets the same answer.
    /// Expired mappings are deleted like any other.
    /// </summary>
    public async Task<ManagementOutcome> DeleteAsync(string code, CancellationToken cancellationToken)
    {
        if (!ShortCodeFormat.IsValid(code))
        {
            return new ManagementOutcome.NotFound();
        }

        var mapping = await FindAsync(code, cancellationToken);
        if (mapping is null)
        {
            return new ManagementOutcome.NotFound();
        }

        // Already inactive: nothing to write, but the cache entry is still removed, which also
        // repairs an invalidation that failed on the first delete.
        if (mapping.IsActive)
        {
            mapping.IsActive = false;
            if (!await TrySaveAsync(mapping, cancellationToken))
            {
                return new ManagementOutcome.Conflict();
            }
        }

        await InvalidateCacheAsync(code);
        return new ManagementOutcome.Deleted();
    }

    /// <summary>
    /// Runs after the SQL commit. Removing before the commit would let a concurrent redirect
    /// re-cache the old row before the new one is visible.
    /// </summary>
    private async Task InvalidateCacheAsync(string code)
    {
        try
        {
            await _cache.RemoveAsync(code);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Redirect cache invalidation failed for {ShortCode}; the change is saved, but redirects may " +
                "use the previous value until the cache entry expires.",
                code);
        }
    }

    private async Task<UrlMapping?> FindAsync(string code, CancellationToken cancellationToken)
    {
        // Tracked, so SaveChanges issues "UPDATE ... WHERE Id = @id AND RowVersion = @original".
        // The ShortCode column's binary collation makes the SQL comparison case-sensitive.
        var mapping = await _dbContext.UrlMappings
            .Where(m => m.ShortCode == code)
            .FirstOrDefaultAsync(cancellationToken);

        // Same ordinal re-check as ShortCodeResolver, in case the column collation changes.
        return mapping is not null && string.Equals(mapping.ShortCode, code, StringComparison.Ordinal)
            ? mapping
            : null;
    }

    /// <returns>False when the row changed since it was read (rowversion mismatch).</returns>
    private async Task<bool> TrySaveAsync(UrlMapping mapping, CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.Entry(mapping).State = EntityState.Detached;
            _logger.LogInformation(
                "Short code {ShortCode} was modified concurrently; the change was not saved.",
                mapping.ShortCode);
            return false;
        }
        catch
        {
            // Leave the context clean for anything else sharing this scope.
            _dbContext.Entry(mapping).State = EntityState.Detached;
            throw;
        }
    }
}
