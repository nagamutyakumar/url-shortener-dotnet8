# Health checks

Two endpoints built on the ASP.NET Core health checks (`Microsoft.Extensions.Diagnostics.HealthChecks`
and `MapHealthChecks`, both in the .NET 8 shared framework, so no new package). Code:
`src/UrlShortener.Api/Health/`.

| Endpoint | Purpose | Checks run | Status codes |
|---|---|---|---|
| `GET /health/live` | Liveness: is the process up and serving HTTP? | none | `200` |
| `GET /health/ready` | Readiness: can this instance serve traffic? | `sql`, plus `redis` when configured | `200` Healthy or Degraded, `503` Unhealthy |

Both also answer `HEAD`; other methods get `405`.

## Liveness versus readiness

- **Liveness** says whether the process should be restarted. It runs no checks and never touches
  SQL Server or Redis, so a database outage cannot make an orchestrator restart every instance
  (which would not fix the database and would add a restart storm on top).
- **Readiness** says whether the instance should receive traffic. When it fails, a load balancer
  or Kubernetes takes the instance out of rotation without restarting it, and puts it back once
  readiness passes again.

## Dependency behaviour

| Check | When registered | On failure | Timeout | How |
|---|---|---|---|---|
| `sql` | always | **Unhealthy** → `503` | 3 s | Own non-pooled `SqlConnection` (2 s connect, 2 s command timeout), `SELECT 1` |
| `redis` | only when `ConnectionStrings:Redis` is set | **Degraded** → `200` | 2 s | `IsConnected` on the app's own multiplexer, then `PING` |

- SQL Server is the source of truth: without it no redirect, shorten or analytics request can
  succeed, so the instance is not ready.
- The SQL check builds its own connection string from `ConnectionStrings:UrlShortener` (same
  server, database and credentials) with `Connect Timeout=2`, `Command Timeout=2`,
  `ConnectRetryCount=0` and `Pooling=false`, then opens a connection and runs `SELECT 1`. The
  application's `DbContext` connection string, pool and timeouts are unchanged. See
  [SQL check timeout](#sql-check-timeout) for why it does not use `DbContext.CanConnectAsync`.
- The Redis check reuses the singleton `IConnectionMultiplexer`; it does not open a new
  connection per probe. While the multiplexer is disconnected it reports Degraded immediately
  without sending a command.
- Checks run in parallel, so a readiness request takes at most about the longest timeout (3 s).
- A timeout cancels the check's token and is reported with the check's failure status. A probe
  whose client disconnects cancels the checks through the request's abort token.
- The registration timeout is **cooperative**: the health check service cancels the token but
  still awaits the check. Both checks therefore wrap their I/O in `WaitAsync(cancellationToken)`
  so a client library that ignores the token cannot hold the response open.

### SQL check timeout

The first version called `UrlShortenerDbContext.Database.CanConnectAsync`. In a live test with
the SQL Server container stopped, readiness correctly answered `503`, but after **14.7 s**
(`sql` duration 14,724 ms) despite the 3 s timeout:

- The application connection string sets no `Connect Timeout`, so SqlClient uses its 15 s default.
- The check's duration matched that 15 s, so the open ran until SqlClient gave up, not until the
  3 s token fired. In isolated tests SqlClient 5.1 did honour the token for a fresh pool (refused
  port, unroutable address, a listener that accepts and never answers), so the open that ignored
  it most likely went through the shared pool's synchronous path, which already held connections
  to the stopped server. That could not be reproduced without stopping SQL Server.
- `DefaultHealthCheckService` does not bound a check that ignores its token: a check that sleeps
  2 s with a 100 ms registration timeout took 2,004 ms.
- No EF Core retrying execution strategy is configured (`EnableRetryOnFailure` is not used), so
  retries did not contribute.

The fix bounds the probe twice without changing application database behaviour: SqlClient's own
2 s connect and command timeouts on a separate, non-pooled connection (these hold even if the
token is ignored), and `WaitAsync` on the check's token so the 3 s registration timeout always
ends the wait. A probe abandoned by `WaitAsync` finishes on its own within those SqlClient
timeouts.

### Redis degraded behaviour

Redis is an optional cache with SQL fallback (see [caching.md](caching.md)). When Redis is
configured but unreachable, redirects still work from SQL Server, only slower, so readiness
answers **`200` with `"status": "Degraded"`** rather than `503`. Taking every instance out of
rotation because the cache is down would turn a performance problem into an outage.

When Redis is not configured, there is no `redis` entry at all: it is neither a failure nor a
misleading "Healthy".

Overall status is the worst entry: SQL Unhealthy with Redis Degraded is Unhealthy (`503`).

## Response

`application/json`, sent with `Cache-Control: no-store, no-cache` by the health check middleware.

```json
{
  "status": "Degraded",
  "totalDurationMs": 4.2,
  "checks": {
    "sql": { "status": "Healthy", "durationMs": 3.9 },
    "redis": { "status": "Degraded", "durationMs": 0.1 }
  }
}
```

`/health/live` always returns `"status": "Healthy"` with an empty `checks` object.

Only statuses and durations are returned. The health check service puts an exception's message
in the entry's description, and messages from SqlClient or StackExchange.Redis can contain host
names, endpoints or connection-string fragments, so descriptions, exceptions and the `data`
dictionary are never written. Failures are logged server-side by the health check service
(category `Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService`).

## Rate limiting

Both endpoints call `.DisableRateLimiting()`. There is no global limiter today, so this changes
nothing now, but it keeps probes exempt if one is added later: a probe that gets `429` would
look like an outage.

## Docker and Kubernetes probes

```yaml
livenessProbe:
  httpGet: { path: /health/live, port: 8080 }
  periodSeconds: 10
  timeoutSeconds: 2
  failureThreshold: 3
readinessProbe:
  httpGet: { path: /health/ready, port: 8080 }
  periodSeconds: 10
  timeoutSeconds: 5     # above the 3 s SQL check timeout
  failureThreshold: 3
```

- Set the readiness probe timeout above the SQL check timeout (3 s); Kubernetes defaults to 1 s,
  which would report a slow-but-working database as a probe failure.
- Probe over plain HTTP inside the container network. `UseHttpsRedirection` runs for every
  request, so if an HTTPS port is configured, an HTTP probe gets a `307` (Kubernetes treats
  2xx–3xx as success, but other tools may not). Terminate TLS at the ingress, or probe the HTTPS port.
- Docker `HEALTHCHECK` has a single probe; point it at `/health/live` if the container should be
  restarted, or at `/health/ready` only for status reporting. The ASP.NET Core runtime images do
  not include `curl`, so it needs to be installed or replaced by a small probe binary.
  `docker-compose.yml` probes `/health/ready` for status reporting using bash's `/dev/tcp`
  (see [docker.md](docker.md)).
- A startup probe, if used, can point at `/health/ready` to hold traffic until the database is
  reachable.

## Limitations and production recommendations

- **Unauthenticated.** The endpoints reveal only check names and statuses, but should still be
  reachable only from the orchestrator or internal network (e.g. not routed by the public
  ingress), to avoid giving away dependency state and to keep probe traffic off the internet.
  `RequireHost` or a separate management port are options.
- **Every readiness probe opens a SQL connection** (from the pool) and pings Redis. At a 10 s
  period this is negligible; with many instances and short periods, add caching of results or use
  `AddHealthCheckPublisher` to run checks in the background.
- **`SELECT 1` proves login and database access**, not that the schema is migrated or that
  application queries are fast. A slow database within the timeout still reports Healthy.
- **The probe does not see the application's pool.** It logs in fresh each time (one login per
  probe period, negligible at 10 s), so it cannot detect pool exhaustion in the app's own pool.
- **2 s connect timeout** suits SQL Server on the same network. A remote or cold-starting server
  (e.g. Azure SQL serverless resume) may need longer; raise `ConnectTimeoutSeconds` and keep it
  below `SqlServerTimeout`.
- **Timeouts are constants** (`HealthCheckEndpoints.SqlServerTimeout`, `RedisTimeout`). Make them
  configuration if environments need different values.
- **A SQL outage reports `503` after at most about 3 s** (the registration timeout, enforced by
  `WaitAsync`), usually about 2 s (the probe's connect timeout). The abandoned SqlClient work ends
  within about 4 s (connect plus command timeout).
- **The click-analytics background writer is not checked.** It is best-effort by design (see
  [analytics.md](analytics.md)); a dropped click should not take an instance out of rotation.
- **Redis is reported as Degraded, never Unhealthy**, so readiness never alerts on a Redis
  outage. Alert on the Redis connection-failure logs or on the `Degraded` status from monitoring.
