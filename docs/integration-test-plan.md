# Integration test plan: POST /api/v1/shorten

The unit tests use the EF Core InMemory provider, which does **not** enforce unique
indexes, SQL Server collations, `rowversion` generation or `datetime2(3)` rounding.
Short-code collisions are simulated with an interceptor and a fake
`IShortCodeConflictDetector`. The behaviours below can only be proven against a real
SQL Server (e.g. a Testcontainers `mcr.microsoft.com/mssql/server` instance with the
`InitialCreate` migration applied) and are not yet covered.

Suggested harness: a separate `UrlShortener.IntegrationTests` project, opt-in (for
example `[Trait("Category", "Integration")]`, excluded from the default `dotnet test`
run), using `WebApplicationFactory<Program>` (requires adding `public partial class Program;`) with the connection string supplied via
environment variable, never committed.

| # | Scenario | Expected |
|---|----------|----------|
| 1 | Insert a row with short code `Abc1234`, then make the generator return `Abc1234`, then a fresh code | SQL error 2601 with `UX_UrlMappings_ShortCode` in the message; `SqlServerShortCodeConflictDetector` returns true; request returns 201 with the second code |
| 2 | Generator always returns an existing code | 503 problem response after `MaxShortCodeAttempts`; no row inserted |
| 3 | Insert a row whose code is `abc1234` and generate `ABC1234` | Both succeed (binary collation is case-sensitive) |
| 4 | Force a non-unique failure (e.g. a URL longer than 2048 chars written directly through the context) | `DbUpdateException` is not classified as a conflict; no retry |
| 5 | Shorten `https://example.com/Docs` then `https://example.com/docs` | Two mappings: the in-memory ordinal comparison overrides the column's case-insensitive collation |
| 6 | Shorten with `expiresAtUtc` = `...T00:00:00.1239Z`, then repeat the same request | Stored as `.123`; second request returns 200 with the same code (no rounding mismatch) |
| 7 | Read back `CreatedAtUtc`, `UpdatedAtUtc`, `ExpiresAtUtc` | `DateTimeKind.Utc`, timestamps from the injected `TimeProvider` |
| 8 | 20 concurrent requests for the same new URL | All succeed; may create more than one mapping (accepted race, documented on `UrlShorteningService`); every returned code resolves to the URL |
| 9 | Malformed JSON body, malformed `expiresAtUtc`, and `expiresAtUtc` without `Z`/offset | Covered in-process by `ShortenHttpTests` (Development and Production): 400 with a generic problem-details body, no per-field errors. Only re-check here against the real database setup |

# Integration test plan: GET /{code}

`ShortCodeResolverTests` and `RedirectHttpTests` run on InMemory, whose string `==` is
always ordinal and whose `DateTime` comparison uses exact ticks. They prove the resolver's
logic and the HTTP contract (302 + `Location`, generic 404), but **not** the SQL Server
behaviour below. Same harness as above; seed rows directly through the context and call
`GET /{code}` with `AllowAutoRedirect = false` so the destination is never contacted.

| # | Scenario | Expected |
|---|----------|----------|
| R1 | Seed `Abc1234`; request `/abc1234`, `/ABC1234`, `/Abc1234` | 404, 404, 302. Proves the `Latin1_General_100_BIN2` column collation drives the `WHERE ShortCode = @code` comparison (the resolver's ordinal re-check would also hide a case-insensitive match, so additionally assert with SQL Profiler / `LogTo` that the generated SQL returns no row for the wrong case) |
| R2 | Seed both `abc1234` and `ABC1234` with different URLs | Each redirects to its own URL |
| R3 | Inspect the generated SQL for the lookup | Single parameterized query on `ShortCode` using `UX_UrlMappings_ShortCode` (index seek); parameter is `varchar(7)`/`char(7)`, not `nvarchar` (an `nvarchar` parameter would force an implicit conversion and a scan) |
| R4 | `ExpiresAtUtc` = `2030-01-01T00:00:00.123`; clock at `.1225`, `.1229`, `.123`, `.1231` | 302, 302, 404, 404. Proves the resolver's millisecond truncation of "now" keeps the boundary exact despite SqlClient's handling of sub-millisecond `datetime2(3)` parameters |
| R5 | `IsActive = 0` row; row with `ExpiresAtUtc` in the past | 404 for both |
| R6 | Malformed codes (`/abc`, `/Abc-123`, `/Abc%2F12`) | 404 and no `SELECT` against `UrlMappings` in the command log |
| R7 | Abort the request mid-query (client disconnect) | Query is cancelled via `HttpContext.RequestAborted`; no unhandled-exception log noise beyond the expected cancellation |

# Integration test plan: click analytics

The analytics tests run on InMemory. `ClickAnalyticsSqlTranslationTests` translate the
aggregate queries with the real SQL Server provider (`ToQueryString`, no connection), which
proves they run server-side. It does not prove what the SQL returns. InMemory cannot show:

- **Distinct hashes.** InMemory's `Distinct()` compares `byte[]` by reference, so equal hashes
  are not merged. The unit tests therefore use only distinct hashes.
- **Foreign keys.** InMemory does not enforce `FK_ClickEvents_UrlMappings_UrlMappingId`.
- **Transactions.** It does not show that a batch insert is all-or-nothing within one transaction.
- **`datetime2(3)` rounding** of `ClickedAtUtc`.
- **Collation-based case sensitivity** of the analytics lookup.
- **SqlClient behaviour** under cancellation, timeouts, connection-pool pressure or a down server.

| # | Scenario | Expected |
|---|----------|----------|
| A1 | Redirect once; read the `ClickEvents` row | One row; `UrlMappingId` matches; `ClickedAtUtc` is the injected clock rounded to `datetime2(3)` with `Kind = Utc`; `VisitorHash` is 32 bytes (secret set, real remote IP) or NULL; `CountryCode` and `Browser` are NULL |
| A2 | Seed 3 clicks with the same `VisitorHash` value (separate arrays), 2 with distinct values, and 1 with NULL | `uniqueVisitors` = `partial`, count 3, `clicksWithVisitorHash` 5. Proves `COUNT_BIG(DISTINCT)` compares `binary(32)` by value and ignores NULLs |
| A3 | Same visitor (same IP and User-Agent, secret set) clicks one link twice and another link once | Link 1: 2 clicks, 1 unique visitor. The two links' hashes differ |
| A4 | Writer batch containing a `UrlMappingId` with no mapping row | FK violation is logged as an error; the **whole batch** is rolled back and discarded; the writer keeps running; later batches persist |
| A5 | Stop SQL Server, then redirect 50 times | Every response is 302 with no added latency from the database; errors are logged per batch; queue drops are logged once the queue is full. After the server restarts, new clicks persist; lost clicks are not replayed |
| A6 | Inspect the SQL and plan for `GET /api/v1/analytics/{code}` | Mapping lookup uses `UX_UrlMappings_ShortCode` with a `varchar` parameter. Summary and country queries seek `IX_ClickEvents_UrlMappingId_ClickedAtUtc` (plus key lookups). No client-side evaluation |
| A7 | Seed `Abc1234`; call analytics for `abc1234` | 404. The collation drives the match, as in R1 |
| A8 | Insert 100 queued clicks with `BatchSize` = 100 | Batched into a few round trips (EF `MERGE … OUTPUT`), not 100 single inserts |
| A9 | Send SIGTERM with clicks buffered | Buffered clicks persist within `ShutdownFlushTimeout` (5 s); otherwise the unwritten count is logged |
