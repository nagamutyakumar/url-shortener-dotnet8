# URL Shortener API

A URL shortening service built with ASP.NET Core 8 minimal APIs, written as a take-home
assessment. It creates seven-character short codes, redirects them, records clicks in the
background and reports per-link analytics. Links can be updated and soft-deleted. SQL Server is
the source of truth. Redis is an optional redirect cache.

The scope is assessment and demo use. The API has **no authentication**; see
[Security and operational limitations](#security-and-operational-limitations).

## Technology stack

| Area | Choice |
|---|---|
| Runtime | .NET 8, ASP.NET Core minimal APIs |
| Persistence | SQL Server, EF Core 8 (`Microsoft.EntityFrameworkCore.SqlServer` 8.0.31), code-first migrations |
| Cache | Redis via `StackExchange.Redis` 2.13.17 (optional) |
| Rate limiting, health checks | Built-in ASP.NET Core middleware (no extra packages) |
| API docs | Swashbuckle / Swagger UI (Development environment only) |
| Tests | xUnit, `Microsoft.AspNetCore.Mvc.Testing`, EF Core InMemory |
| Packaging | Multi-stage `Dockerfile`, Docker Compose (API, migrator, SQL Server 2022, Redis 7) |
| CI | GitHub Actions: restore, build and test on .NET 8 |

## Architecture

One API project (`src/UrlShortener.Api`) and one test project (`tests/UrlShortener.UnitTests`).
Code is organised by feature: `Shortening/`, `Redirection/`, `Analytics/`, `Caching/`,
`RateLimiting/`, `Health/`, `Persistence/`, `ShortCodes/`.

```
                      ┌──────────────────────── UrlShortener.Api ─────────────────────────┐
 POST/PUT/DELETE ───► │ rate limiter ─► UrlShorteningService / ShortUrlManagementService  │──► SQL Server
 /api/v1/shorten      │                       (PUT/DELETE: DEL cache key after commit) ───┼──► Redis
                      │                                                                   │
 GET /{code} ───────► │ ShortCodeResolver: Redis GET ─(miss)─► SQL lookup ─► Redis SET    │
                      │       │ 302                                                       │
                      │       └─► ClickTracker ─► bounded in-memory channel               │
                      │                              └─► ClickEventWriter (hosted) ───────┼──► SQL Server
                      │                                  batched inserts                  │    (ClickEvents)
 GET /api/v1/ ──────► │ rate limiter ─► ClickAnalyticsService (aggregate queries) ────────┼──► SQL Server
 analytics/{code}     │                                                                   │
 /health/* ─────────► │ health checks: sql (always), redis (when configured)              │
                      └───────────────────────────────────────────────────────────────────┘
```

### Request flow

- **Shorten (`POST /api/v1/shorten`).** The URL is validated and canonicalised
  (`UrlCanonicalizer`: absolute http/https only, no credentials, at most 2048 characters). An
  existing active mapping is reused (200) when the canonical URL and expiration both match
  exactly. Otherwise a new code is generated with a CSPRNG over a Base62 alphabet and inserted
  (201). The unique index on `ShortCode` decides collisions, and the insert is retried up to 5
  times before returning 503.
- **Redirect (`GET /{code}`).** Malformed codes return 404 without touching Redis or SQL. If
  Redis is configured, it is read first. A hit is used only when its stored expiration has not
  passed. On a miss, SQL Server is queried (case-sensitive, active, not expired) and the result is
  cached with a TTL of at most `RedirectCache:MaxTimeToLive`. Any Redis error falls back to SQL
  Server. A successful 302 enqueues one click event; the redirect never waits for the database
  write.
- **Click analytics.** `ClickEventWriter` (a `BackgroundService`) drains a bounded `Channel` and
  inserts clicks in batches. Delivery is best-effort: when the queue is full, a batch insert
  fails, or the process stops, clicks are dropped and logged rather than delaying redirects. The
  visitor hash is an HMAC of mapping id, remote IP and User-Agent. The raw IP and User-Agent are
  never stored.
- **Management (`PUT`/`DELETE /api/v1/shorten/{code}`).** Updates and soft deletes use the
  `rowversion` column for optimistic concurrency (409 on conflict). The Redis entry is deleted
  after the SQL commit.

## Endpoints

| Method | Path | Behaviour | Rate limit |
|---|---|---|---|
| `POST` | `/api/v1/shorten` | Body `{ "originalUrl", "expiresAtUtc"? }`. **201** new mapping, **200** reused mapping, **400** validation error, **503** no free code after 5 attempts. | `mutations` |
| `PUT` | `/api/v1/shorten/{code}` | Replace the destination. An omitted `expiresAtUtc` keeps the current value; `null` clears it. **200**, **400**, **404** (unknown, wrong-case or deleted), **409** concurrent change. | `mutations` |
| `DELETE` | `/api/v1/shorten/{code}` | Soft delete (`IsActive = false`); click history is kept. **204** (also for a repeat delete), **404**, **409**. | `mutations` |
| `GET` | `/{code}` | **302** to the original URL. **404** for malformed, unknown, wrong-case, deleted or expired codes. | none |
| `GET` | `/api/v1/analytics/{code}` | Total clicks, unique visitors (`complete` / `partial` / `unavailable`) and a country breakdown. **200** also for deleted or expired links, **404** for malformed, unknown or wrong-case codes. | `analytics` |
| `GET`, `HEAD` | `/health/live` | Liveness; runs no dependency checks. Always **200**. | none |
| `GET`, `HEAD` | `/health/ready` | Readiness: SQL Server (and Redis when configured). **200** Healthy/Degraded, **503** Unhealthy. | none |
| `GET` | `/swagger` | Swagger UI, Development environment only. | none |

- Short codes are 7 characters from `[0-9A-Za-z]`, matched **case-sensitively**
  (`Latin1_General_100_BIN2` column collation plus an ordinal check).
- `expiresAtUtc` must be an ISO 8601 timestamp with `Z` or an explicit offset. It must be in the
  future and is truncated to milliseconds.
- Errors use RFC 7807 problem details. 404 bodies are generic and do not say why a code did not
  resolve.
- Example requests are in [`src/UrlShortener.Api/UrlShortener.Api.http`](src/UrlShortener.Api/UrlShortener.Api.http).

## Quick start (Docker Compose)

Requires Docker Desktop. Commands are for PowerShell, run from the repository root. The full
guide, including troubleshooting, is [docs/docker.md](docs/docker.md).

```powershell
Copy-Item .env.example .env
notepad .env          # set MSSQL_SA_PASSWORD (required); see the comments in the file

docker compose build
docker compose up -d --wait     # sql → migrate (one-shot) → api; returns once api is healthy
docker compose ps -a            # migrate "Exited (0)", the others "(healthy)"
```

`.env` is git-ignored. `.env.example` documents each variable:

| Variable | Required | Purpose |
|---|---|---|
| `MSSQL_SA_PASSWORD` | yes | SQL Server `sa` password (complexity rules and allowed characters are in `.env.example`) |
| `ANALYTICS_VISITOR_HASH_SECRET` | no | ≥ 32 characters; enables unique-visitor counts |
| `API_HOST_PORT` | no | Host port, bound to `127.0.0.1` (default `8080`) |
| `PUBLIC_BASE_URL` | no | Origin used in returned short URLs (default `http://localhost:<API_HOST_PORT>`) |
| `ASPNETCORE_ENVIRONMENT` | no | `Production` (default) or `Development` (enables Swagger) |

Smoke test:

```powershell
Invoke-RestMethod http://localhost:8080/health/ready
$r = Invoke-RestMethod -Method Post http://localhost:8080/api/v1/shorten `
    -ContentType 'application/json' -Body '{"originalUrl":"https://example.com/docs"}'
curl.exe -s -o NUL -w "%{http_code} %{redirect_url}`n" $r.shortUrl    # 302 https://example.com/docs
Invoke-RestMethod "http://localhost:8080/api/v1/analytics/$($r.shortCode)"
```

Stop with `docker compose down`. The SQL data volume is kept; `docker compose down -v` deletes it.

## Local development

Prerequisites: .NET 8 SDK, a reachable SQL Server, and optionally Redis. Connection strings are
read from configuration and are not committed. Use user-secrets or environment variables:

```powershell
dotnet user-secrets set "ConnectionStrings:UrlShortener" "<SQL Server connection string>" --project src/UrlShortener.Api
dotnet user-secrets set "ConnectionStrings:Redis" "localhost:6379,asyncTimeout=500" --project src/UrlShortener.Api   # optional
dotnet user-secrets set "Analytics:VisitorHashSecret" "<at least 32 characters>" --project src/UrlShortener.Api      # optional

# Apply migrations (requires the dotnet-ef tool, version 8.x)
dotnet ef database update --project src/UrlShortener.Api

dotnet run --project src/UrlShortener.Api --launch-profile http   # http://localhost:5210, Swagger at /swagger
```

The API refuses to start without `ConnectionStrings:UrlShortener`. Without
`ConnectionStrings:Redis`, the cache is disabled and every redirect reads SQL Server.

### Tests

```powershell
dotnet test UrlShortener.sln
# Same steps as CI:
dotnet restore UrlShortener.sln
dotnet build UrlShortener.sln --configuration Release --no-restore
dotnet test UrlShortener.sln --configuration Release --no-build
```

The suite (462 tests at the time of writing) needs no SQL Server, Redis, Docker or secrets.
`ApiFactory` hosts the real `Program` pipeline on EF Core InMemory. It also clears
`ConnectionStrings:Redis` and raises the rate limits, so local secrets cannot change test
results. InMemory does not enforce unique indexes, collations, `rowversion` values or
`datetime2(3)` rounding. Those behaviours are listed as manual and future integration scenarios in
[docs/integration-test-plan.md](docs/integration-test-plan.md); they are **not** automated.

## Configuration

| Section | Key settings (defaults in `appsettings.json`) | Details |
|---|---|---|
| `ConnectionStrings` | `UrlShortener` (required), `Redis` (optional) | user-secrets or environment variables |
| `ShortUrls` | `PublicBaseUrl` (unset: request scheme and host are used) | validated at startup |
| `Analytics` | `QueueCapacity` 10000, `BatchSize` 100, `VisitorHashSecret` (unset) | [analytics.md](docs/analytics.md) |
| `RedirectCache` | `MaxTimeToLive` 00:05:00 | [caching.md](docs/caching.md) |
| `RateLimiting` | `Mutations` 20/min, `Analytics` 60/min, `QueueLimit` 0 | [rate-limiting.md](docs/rate-limiting.md) |

Options are validated with `ValidateOnStart`, so an out-of-range value stops the application at
startup instead of being ignored at runtime.

## Operations

**Health checks.** `/health/live` checks no dependencies, so a database outage cannot cause
restart loops. `/health/ready` runs a `SELECT 1` against SQL Server on its own non-pooled
connection, with a 2 s connect timeout, a 2 s command timeout and a 3 s check timeout. A SQL
failure returns 503. Redis is checked only when configured, and a failure reports `Degraded`
(still 200) because redirects fall back to SQL. Responses include only statuses and durations.
See [docs/health-checks.md](docs/health-checks.md).

**Rate limiting.** Fixed-window, per client IP (`RemoteIpAddress`; `X-Forwarded-For` is
deliberately ignored). POST, PUT and DELETE share the `mutations` budget, and analytics has its
own. Redirects and health checks are not limited. Rejections are 429 problem details with
`Retry-After`. Counters are in memory per instance. See
[docs/rate-limiting.md](docs/rate-limiting.md).

**Migrations.** One EF Core migration (`InitialCreate`) creates `UrlMappings` and `ClickEvents`.
The API never migrates at startup. In Compose, the `migrate` service runs an EF Core migration
bundle built from the `migrator` Dockerfile target. It runs after SQL Server is healthy, and the
API starts only after it exits with code 0. It is idempotent and runs on every `up`. Locally, run
`dotnet ef database update`.

**CI.** [`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on pushes and pull requests
to `main`. It restores, builds in Release and runs the tests on `ubuntu-latest`, with read-only
permissions, no secrets and a 15-minute timeout. A `.trx` artifact is uploaded on failure. It
does not deploy or publish images. See [docs/ci.md](docs/ci.md).

## Design decisions and tradeoffs

- **Random codes, not sequential ids.** Seven Base62 characters from a CSPRNG (about 3.5 × 10¹²
  combinations). Codes cannot be enumerated from an id. The database unique index enforces
  uniqueness, and there is no check before insert.
- **Conservative URL canonicalisation.** Only RFC 3986 equivalence-preserving normalisation is
  applied (scheme and host case, default port, dot segments, punycode). Path and query case,
  trailing slashes and parameter order are kept, so two URLs that a server might treat
  differently are never merged.
- **Best-effort deduplication.** Lookup uses a non-unique SHA-256 hash index plus an ordinal
  comparison. Concurrent first requests for the same URL can create two codes. Both work.
- **Soft delete with retained history.** `ClickEvents → UrlMappings` uses `Restrict`. Deleted
  codes keep their slot and are never reissued. Analytics still answer for deleted links.
- **Asynchronous, lossy click tracking.** Redirect latency does not depend on the database
  write. In exchange, clicks can be lost on queue overflow, insert failure or a crash. A durable
  outbox or message broker was considered out of scope.
- **Redis as an optional cache.** The API starts and serves traffic without Redis (`AbortOnConnectFail = false`,
  `BacklogPolicy = FailFast`). Consistency is eventual, and staleness after PUT/DELETE is bounded
  by `MaxTimeToLive` (5 min by default). An expired link never redirects from cache, because the
  stored expiration is checked on every hit. Unknown codes are not cached.
- **Optimistic concurrency only within a request.** `rowversion` prevents lost updates between
  a request's read and its write. There is no ETag/`If-Match` between requests, so the last
  writer wins.
- **Migrations outside the API process.** Several API instances never race to migrate, and
  `dotnet run` does not change the schema.

## Security and operational limitations

- **No authentication or authorisation.** Anyone who knows a code can change its destination,
  delete it, or read its analytics. Because POST reuses mappings, two callers who shorten the
  same URL share a code and can change each other's link.
- **No destination safety checks.** URLs are validated syntactically only. There is no
  malware/phishing screening, no domain blocklist, and redirects to private or internal
  addresses are not blocked.
- **Proxy awareness is off.** Forwarded headers are not processed. Behind a reverse proxy, every
  client shares the proxy's rate-limit budget and visitor hashes collapse to one IP. Short URLs use
  the request host unless `ShortUrls:PublicBaseUrl` is set.
- **Rate limits are per instance** and in memory. They are not shared across replicas and reset
  on restart.
- **Analytics are approximate.** Clicks are lost when the queue overflows or the process crashes,
  and bots are counted. `CountryCode` is always `null` because no geolocation source is
  configured, and `Browser` is not populated.
- **The Docker setup is for local use.** The API connects as `sa`, secrets live in a `.env`
  file, Redis has no password, SQL Server uses `TrustServerCertificate=True`, the container
  serves plain HTTP, and image tags (`2022-latest`, `7-alpine`) are not pinned.
- **Health endpoints are unauthenticated** and should not be exposed publicly.
- **No SQL Server integration tests.** Collation, unique-index, `rowversion` and transaction
  behaviour is covered only by the written plan in `docs/integration-test-plan.md` and by manual
  checks.
- **No observability beyond logs.** No metrics, tracing or alerting is configured.

## Further documentation

| Document | Covers |
|---|---|
| [docs/analytics.md](docs/analytics.md) | Click collection pipeline, delivery guarantees, visitor hashing, analytics API |
| [docs/management.md](docs/management.md) | PUT/DELETE semantics, soft delete, concurrency |
| [docs/caching.md](docs/caching.md) | Redis cache-aside, invalidation, consistency bounds |
| [docs/rate-limiting.md](docs/rate-limiting.md) | Policies, configuration, 429 response, proxy considerations |
| [docs/health-checks.md](docs/health-checks.md) | Liveness vs readiness, timeouts, SQL timeout investigation |
| [docs/docker.md](docs/docker.md) | Compose stack, `.env`, troubleshooting |
| [docs/ci.md](docs/ci.md) | GitHub Actions workflow and reading failures |
| [docs/integration-test-plan.md](docs/integration-test-plan.md) | SQL Server and Redis scenarios not covered by the in-process tests |
| [docs/ai-collaboration.md](docs/ai-collaboration.md) | How AI assistance was used and how its output was reviewed |
