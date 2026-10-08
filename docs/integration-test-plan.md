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
