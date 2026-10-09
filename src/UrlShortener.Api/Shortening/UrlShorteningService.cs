using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Persistence.Entities;
using UrlShortener.Api.ShortCodes;

namespace UrlShortener.Api.Shortening;

/// <summary>
/// Creates short-code mappings, reusing an existing one for an equivalent URL.
/// </summary>
/// <remarks>
/// Duplicate policy: an existing mapping is reused only when it is active, not yet
/// expired, has the same canonical URL, and has exactly the same expiration as the
/// request (both none, or the same instant to the millisecond). Otherwise a new
/// mapping is created. Creating never modifies an existing mapping, so a POST can never
/// shorten or extend the lifetime of a link someone else already shared. (The management
/// endpoints in <see cref="ShortUrlManagementService"/> can, and because of reuse a code
/// may be shared by several callers; see docs/management.md.)
/// Deduplication is best-effort: the hash index is not unique, so concurrent first-time
/// requests for the same URL may each create a mapping. Every such code still works.
/// </remarks>
public sealed class UrlShorteningService
{
    public const int MaxShortCodeAttempts = 5;

    private readonly UrlShortenerDbContext _dbContext;
    private readonly IShortCodeGenerator _shortCodeGenerator;
    private readonly IShortCodeConflictDetector _conflictDetector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UrlShorteningService> _logger;

    public UrlShorteningService(
        UrlShortenerDbContext dbContext,
        IShortCodeGenerator shortCodeGenerator,
        IShortCodeConflictDetector conflictDetector,
        TimeProvider timeProvider,
        ILogger<UrlShorteningService> logger)
    {
        _dbContext = dbContext;
        _shortCodeGenerator = shortCodeGenerator;
        _conflictDetector = conflictDetector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<ShortenOutcome> ShortenAsync(
        ShortenUrlRequest request,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var errors = new Dictionary<string, string[]>();
        if (!UrlCanonicalizer.TryCanonicalize(request.OriginalUrl, out var canonicalUrl, out var urlError))
        {
            errors[nameof(ShortenUrlRequest.OriginalUrl)] = new[] { urlError };
        }

        if (!ExpirationRules.TryNormalize(request.ExpiresAtUtc, now, out var expiresAtUtc, out var expirationError))
        {
            errors[nameof(ShortenUrlRequest.ExpiresAtUtc)] = new[] { expirationError };
        }

        if (errors.Count > 0)
        {
            return new ShortenOutcome.Invalid(errors);
        }

        var hash = UrlCanonicalizer.ComputeHash(canonicalUrl!);

        var existing = await FindReusableMappingAsync(hash, canonicalUrl!, expiresAtUtc, now, cancellationToken);
        if (existing is not null)
        {
            return new ShortenOutcome.Reused(existing);
        }

        return await CreateMappingAsync(canonicalUrl!, hash, expiresAtUtc, cancellationToken);
    }

    private async Task<UrlMapping?> FindReusableMappingAsync(
        byte[] hash,
        string canonicalUrl,
        DateTime? expiresAtUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var candidates = _dbContext.UrlMappings
            .AsNoTracking()
            .Where(m => m.OriginalUrlHash == hash
                && m.IsActive
                && m.ExpiresAtUtc == expiresAtUtc
                // Implied by the exact-match rule plus "requested expiry is in the future";
                // kept explicit so a looser expiry policy cannot return an expired link.
                && (m.ExpiresAtUtc == null || m.ExpiresAtUtc > nowUtc))
            .OrderBy(m => m.Id)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken);

        // Compared in memory: the OriginalUrl column uses the database's default collation,
        // which is typically case-insensitive, and URL paths are case-sensitive. Rows are
        // streamed rather than capped, so every hash match is checked and reading stops at
        // the first exact match.
        await foreach (var candidate in candidates)
        {
            if (string.Equals(candidate.OriginalUrl, canonicalUrl, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<ShortenOutcome> CreateMappingAsync(
        string canonicalUrl,
        byte[] hash,
        DateTime? expiresAtUtc,
        CancellationToken cancellationToken)
    {
        // The unique index is authoritative; no pre-check query, just retry on collision.
        for (var attempt = 1; attempt <= MaxShortCodeAttempts; attempt++)
        {
            var mapping = new UrlMapping
            {
                ShortCode = _shortCodeGenerator.Generate(),
                OriginalUrl = canonicalUrl,
                OriginalUrlHash = hash,
                ExpiresAtUtc = expiresAtUtc,
                IsActive = true,
            };

            _dbContext.UrlMappings.Add(mapping);

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                return new ShortenOutcome.Created(mapping);
            }
            catch (DbUpdateException ex) when (_conflictDetector.IsShortCodeConflict(ex))
            {
                _dbContext.Entry(mapping).State = EntityState.Detached;
                _logger.LogWarning(
                    "Short code collision on attempt {Attempt} of {MaxAttempts}.",
                    attempt,
                    MaxShortCodeAttempts);
            }
            catch
            {
                // Leave the context clean for anything else sharing this scope.
                _dbContext.Entry(mapping).State = EntityState.Detached;
                throw;
            }
        }

        _logger.LogError(
            "Could not allocate a unique short code after {MaxAttempts} attempts.",
            MaxShortCodeAttempts);
        return new ShortenOutcome.ShortCodeUnavailable();
    }
}
