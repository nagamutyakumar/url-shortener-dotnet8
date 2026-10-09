# API rate limiting

The management and analytics APIs are rate limited per client IP with the built-in ASP.NET Core
middleware (`Microsoft.AspNetCore.RateLimiting`, part of the .NET 8 shared framework, so no new
package). Code: `src/UrlShortener.Api/RateLimiting/`.

## Policies

| Policy | Endpoints | Default |
|---|---|---|
| `mutations` | `POST /api/v1/shorten`, `PUT /api/v1/shorten/{code}`, `DELETE /api/v1/shorten/{code}` | 20 requests / 60 s per client |
| `analytics` | `GET /api/v1/analytics/{code}` | 60 requests / 60 s per client |
| none | `GET /{code}` redirects, Swagger | not limited |

- **Fixed window.** The simplest algorithm the middleware offers, and easy to reason about: each
  client gets `PermitLimit` requests per window, and the count resets when the window ends.
- **One budget for all mutations.** POST, PUT and DELETE use the same named policy, so they draw
  from the same counter for a given client: 10 POSTs and 10 DELETEs use up the 20.
- **Policies are independent.** Using up the mutation budget does not affect analytics, and the
  reverse.
- **Every request counts**, including ones that then fail with 400, 404 or 409. The limiter runs
  before the endpoint, so a rejected request never reaches SQL Server, Redis or click tracking,
  and changes nothing.
- **Redirects are not limited.** They are the hot, public path and are already served from
  Redis. A per-IP limit there would mostly hurt many users behind one NAT or proxy. Abuse of
  redirects is better handled at the edge (CDN/WAF).
- **No queueing** (`QueueLimit: 0`). A request over the limit is rejected at once instead of being
  held open until the next window, so a client over its limit cannot tie up connections.

Endpoints opt in with `.RequireRateLimiting(...)` where they are mapped. There is no global
limiter, so a new endpoint is not limited until it opts in.

## Configuration

`appsettings.json`, overridable per environment or with environment variables such as
`RateLimiting__Mutations__PermitLimit=50`:

```json
"RateLimiting": {
  "Mutations": { "PermitLimit": 20, "Window": "00:01:00", "QueueLimit": 0 },
  "Analytics": { "PermitLimit": 60, "Window": "00:01:00", "QueueLimit": 0 }
}
```

| Setting (per policy) | Allowed | Notes |
|---|---|---|
| `PermitLimit` | 1 – 100 000 | Requests per client per window. |
| `Window` | `00:00:01` – `01:00:00` | `TimeSpan` format. |
| `QueueLimit` | 0 – 1 000 | Keep 0 unless there is a reason to delay rather than reject. |

Values are validated at startup (`ValidateOnStart`). An out-of-range value stops the app with an
`OptionsValidationException` naming the `RateLimiting` section, and a value that is not a number
or time span fails during binding, so a typo cannot silently disable or weaken the limits.
Limits are read once at startup; changing them needs a restart.

## HTTP 429 response

```http
HTTP/1.1 429 Too Many Requests
Content-Type: application/problem+json
Retry-After: 60

{
  "type": "https://tools.ietf.org/html/rfc6585#section-4",
  "title": "Too many requests.",
  "status": 429,
  "detail": "The rate limit for this client was exceeded. Retry the request later."
}
```

- Written through `IProblemDetailsService`, so it has the same shape as the API's other errors
  (`type`, `title`, `status`, `detail`). ASP.NET Core has no default `type` for 429, so it is set
  explicitly.
- It does not name the policy, the limit or the client's address.
- `Retry-After` is whole seconds, rounded up, and is sent only when the limiter reports a retry
  time. The .NET 8 fixed-window limiter reports the full window length rather than the time left
  in the current window, so the value is an upper bound: retrying after it always succeeds, but a
  retry may succeed sooner.

## Client IP and proxies

The partition key is `HttpContext.Connection.RemoteIpAddress`, the address of the direct TCP
peer.

- **IPv4-mapped IPv6** (`::ffff:203.0.113.10`, seen on dual-stack sockets) is reduced to
  `203.0.113.10`, so one client never gets two budgets.
- **`X-Forwarded-For` is not read.** Any client can set it, so trusting it would let a caller
  choose a fresh partition for every request. ASP.NET Core's `UseForwardedHeaders` is not
  enabled.
- **No remote address** (rare in production; e.g. some in-process hosts) falls into a single
  partition named `unknown`. Those requests share one budget rather than going unlimited.

**Behind a reverse proxy or load balancer**, every request arrives from the proxy's address, so
all clients would share one budget and the whole service would be throttled at 20 mutations per
minute. Before deploying behind a proxy, enable `UseForwardedHeaders` with
`ForwardedHeadersOptions.KnownProxies` / `KnownNetworks` set to the proxy's addresses, and call it
before `UseRateLimiter`. `RemoteIpAddress` then holds the real client address, and only that
proxy can set it. This is deliberately not configured here because the deployment topology is
not known.

Other consequences of keying by IP:

- Many users behind one NAT or corporate proxy share a budget.
- An IPv6 client usually controls a whole /64 and can rotate addresses inside it. Keying on the
  /64 prefix would close that gap; it is not done here.

## Per-instance limitation

Counters live in memory in each process. With N instances behind a load balancer, a client can
make up to N × `PermitLimit` requests per window, and counters reset when an instance restarts.
This is acceptable for one instance or as a coarse safeguard. Accurate limits across instances
need a shared store (e.g. a Redis-based limiter) or limiting at the gateway, both out of scope.

## Production considerations

- Configure trusted proxies (above) before deploying behind any proxy, CDN or load balancer.
- Put a gateway, CDN or WAF in front for volumetric protection; an in-process limiter only runs
  after the connection is accepted and the request parsed.
- Fixed windows allow a burst of up to 2 × `PermitLimit` across a window boundary (the end of one
  window and the start of the next). A sliding-window or token-bucket policy smooths this if it
  matters.
- Once the API has authentication, partition mutations by user or API key instead of IP.
- Rejections are logged by the middleware at Debug level only. Add metrics or a log on
  `OnRejected` if 429 rates need monitoring.
- Idle per-client limiters are removed by the middleware, so memory use follows the number of
  recently active clients.

## Tests

`tests/UrlShortener.UnitTests/RateLimiting/` runs the real pipeline with limits of 1–3 requests
and a one-hour window, so no window rolls over during a test and nothing sleeps. TestServer
requests have no remote IP; a test-only startup filter sets `RemoteIpAddress` from a request
header to simulate different clients. `ApiFactory` sets both limits to 100 000 by default, and
each factory has its own limiter state, so the other tests are unaffected by rate limiting.
