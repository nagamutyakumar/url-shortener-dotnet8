# Managing short URLs

> **Unauthenticated.** Anyone who knows (or guesses) a seven-character code can change its
> destination or delete it. That is acceptable only for a controlled assessment or demo, never
> for a public production deployment, which would need authentication plus per-link ownership
> (a `CreatedBy`/owner column checked on every PUT and DELETE). POST deduplication makes this
> worse: two callers who shorten the same URL get the same code, so either can change or delete
> the other's link. With ownership, reuse would have to be scoped per owner.

Both endpoints use the same code matching as redirects and analytics: malformed, unknown and
wrong-case codes get an empty 404 (generic problem details), and no query runs for a malformed
code. Lookups use the `ShortCode` column's binary collation plus an ordinal re-check, and the
UPDATE targets the primary key, so a code that differs only in case is never modified.

## `PUT /api/v1/shorten/{code}`

```json
{ "originalUrl": "https://example.com/new", "expiresAtUtc": "2030-01-01T00:00:00Z" }
```

| Field | Rule |
|---|---|
| `originalUrl` | **Required.** Same validation and canonicalization as POST (`UrlCanonicalizer`: absolute http/https, valid host, no credentials, no whitespace, ≤ 2048 characters before and after normalization). `OriginalUrlHash` is recomputed so later POST deduplication matches the new URL. |
| `expiresAtUtc` **omitted** | The current expiration is kept. |
| `expiresAtUtc: null` | The expiration is cleared; the link never expires. |
| `expiresAtUtc: "<timestamp>"` | Same as POST: must end in `Z` or `±hh:mm` (otherwise 400 during binding), must be in the future, converted to UTC and truncated to milliseconds. |

The request type records whether `expiresAtUtc` appeared in the JSON (System.Text.Json calls
the setter only for properties present in the body), so no JSON Patch / Merge Patch library is
needed. Strictly, a PUT replaces the whole resource; "omitted keeps the current value" is a
deliberate, documented exception, so a client changing only the URL cannot clear an expiration
by accident.

| Status | When |
|---|---|
| 200 | Updated. Body has the same shape as the POST response (`shortCode`, `shortUrl`, `originalUrl`, `expiresAtUtc`). A body that changes nothing writes nothing (EF sees no modified properties) and still returns 200. |
| 400 | Unreadable JSON or a malformed timestamp (generic problem details), or field validation errors (`errors.OriginalUrl`, `errors.ExpiresAtUtc`). The body is validated before the lookup, so an invalid body for an unknown well-formed code gets 400, not 404. |
| 404 | Malformed, unknown or wrong-case code, or an **inactive (deleted)** mapping. A PUT cannot bring back a deleted link. |
| 409 | The row changed between this request's read and write (see Concurrency). Nothing was saved. |

**Expired but still active** links can be updated. A new future expiration (or `null`) makes
the link redirect again. If `expiresAtUtc` is omitted, the past expiration is kept and the link
stays expired. The response's `expiresAtUtc` shows this.

Unchanged by PUT: the short code, `Id`, `CreatedAtUtc`, `IsActive`, and every `ClickEvents` row
(they reference `Id`). Analytics keep counting across the change. `UpdatedAtUtc` is stamped by
`UrlShortenerDbContext`. Redirects are 302 and not cacheable by default, so the new destination
takes effect on the next request once the redirect cache entry has been removed (see
`caching.md`: invalidation is best-effort and staleness is bounded by
`RedirectCache:MaxTimeToLive`).

## `DELETE /api/v1/shorten/{code}`

Soft delete: sets `IsActive = false`. The row and its click events are kept (`ClickEvents` →
`UrlMappings` is `Restrict`).

| Status | When |
|---|---|
| 204 | The mapping is now inactive. **Repeat deletes also return 204** and write nothing (`UpdatedAtUtc` and `RowVersion` stay as they are), so a client can safely retry after a lost response. Expired but active mappings are deleted like any other. |
| 404 | Malformed, unknown or wrong-case code. |
| 409 | The row changed between this request's read and write. Nothing was saved; retrying returns 204. |

After a delete, `GET /{code}` returns 404 like any other non-resolving code, and
`GET /api/v1/analytics/{code}` keeps returning 200 with `linkStatus: "inactive"` and the full
history. Clicks already queued when the link was deleted are still persisted, since the row
exists. A deleted code is never reissued: the row keeps its slot in the unique index. There is
no undelete endpoint.

Repeat-delete returning 204 does not reveal more than before: analytics already show that an
inactive code exists.

## Concurrency

`UrlMapping.RowVersion` is a SQL Server `rowversion` configured with `IsRowVersion()`. Both
endpoints load the tracked entity, change it, and save. EF issues
`UPDATE ... WHERE Id = @id AND RowVersion = @original`. If another writer changed the row in
between, zero rows are affected, EF throws `DbUpdateConcurrencyException`, and the API returns
**409** problem details. Nothing is retried or merged automatically, so a concurrent change is
never silently overwritten. In particular, a PUT racing a DELETE cannot revive the deleted link.
Click tracking only inserts `ClickEvents` rows and never touches `UrlMappings`, so redirects
never cause conflicts.

**Limitation: there is no HTTP concurrency token (ETag / If-Match).** The rowversion check
covers only the read-to-write window inside one request (milliseconds). Between two separate
requests the last writer wins: if A and B both decide to change a link, both PUTs succeed in
turn and B overwrites A. Preventing that needs the client to prove which version it saw:

1. a `GET /api/v1/shorten/{code}` that returns the mapping with `ETag: "<base64 rowversion>"`
   (and the same header on POST/PUT responses);
2. `If-Match` on PUT/DELETE, with the rowversion from the header used as the EF original value;
3. **412 Precondition Failed** on mismatch, and optionally **428 Precondition Required** when
   the header is missing.

That was left out of this milestone. There is no read endpoint to obtain the ETag from, and
without authentication anyone can send a fresh `If-Match` anyway, so the token would protect
only against accidental overwrites by well-behaved clients. It is the natural next step
alongside authentication.

## Tests

- `ShortUrlManagementServiceTests` (InMemory): validation, the three expiration states,
  case-sensitive matching (including `abc1234` and `ABC1234` side by side), inactive and expired
  mappings, repeat delete, no-op updates, preserved click history, and real rowversion
  conflicts. Another context changes the row just before the service saves; the InMemory
  provider checks the concurrency token and the other writer's change survives.
- `ShortUrlManagementHttpTests` (full pipeline): JSON binding of omitted vs `null`
  expiration, 200/204/400/404/409/500 contracts, redirects and analytics after PUT/DELETE.
- SQL Server-only behaviour is listed in `docs/integration-test-plan.md` (M1–M8).
