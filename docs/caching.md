# Redirect caching (Redis)

SQL Server is the source of truth. Redis is an optional, best-effort cache in front of the
redirect lookup in `GET /{code}`. Nothing else reads from it.

## Enabling it

Redis is used only when `ConnectionStrings:Redis` is set. Without it, `NullRedirectCache` is
registered and every redirect reads SQL Server, exactly as before. The connection string can hold
a password, so it is never committed:

```powershell
# Local Redis without a password
dotnet user-secrets set "ConnectionStrings:Redis" "localhost:6379,asyncTimeout=500" --project src/UrlShortener.Api

# Or per process
$env:ConnectionStrings__Redis = "localhost:6379,asyncTimeout=500"
```

Any [StackExchange.Redis option](https://stackexchange.github.io/StackExchange.Redis/Configuration)
can go in the string (`password=`, `ssl=true`, `defaultDatabase=`). Two are always overridden in
code (`RedirectCacheServiceCollectionExtensions.CreateConfigurationOptions`):

| Setting | Forced value | Why |
|---|---|---|
| `AbortOnConnectFail` | `false` | The API starts even when Redis is down and keeps reconnecting in the background. |
| `BacklogPolicy` | `FailFast` | While disconnected, commands fail at once instead of queueing until `asyncTimeout`, so an outage adds no latency. |

`asyncTimeout` (default 5000 ms) bounds a call when Redis is connected but slow. A low value such
as 500 ms is recommended for the redirect path.

| Setting (`RedirectCache:`) | Default | Notes |
|---|---|---|
| `MaxTimeToLive` | `00:05:00` | 1 s – 1 day. Upper bound on entry lifetime, and so on staleness (see below). |

Package: `StackExchange.Redis` 2.13.17. The 3.x line requires .NET 10 versions of
`Microsoft.Extensions.Logging.Abstractions` and `System.IO.Pipelines`, which would replace the
ASP.NET Core 8 shared-framework versions, so it was not used. `IDistributedCache`
(`Microsoft.Extensions.Caching.StackExchangeRedis`) was not used either. It stores hashes with
sliding-expiration metadata and hides connection behaviour, while three plain commands
(`GET`, `SET PX`, `DEL`) are all this needs.

## What is cached

| | |
|---|---|
| Key | `urlshortener:redirect:v1:{code}`. Redis keys are compared byte-for-byte, so `abc1234` and `Abc1234` are different keys. `v1` lets a future value format use new keys. |
| Value | JSON `{"UrlMappingId":42,"OriginalUrl":"https://…","ExpiresAtUtc":"2030-01-01T00:00:00.123Z"}`: the id (for click tracking), the canonical destination, and the expiration (re-checked on every hit). Anything that does not parse as that shape is a miss. |
| TTL | `min(MaxTimeToLive, ExpiresAtUtc − now)`. Not cached when less than 1 ms remains. |
| Never cached | Malformed codes (rejected before Redis is called), unknown or wrong-case codes, inactive (deleted) links, expired links. There is no negative caching. |

## Lookup (cache-aside in `ShortCodeResolver`)

1. Malformed code: 404. No Redis or SQL call.
2. `GET key`. A hit whose `ExpiresAtUtc` is null or still in the future is returned. **A hit past
   its stored expiration is ignored**, so a stale entry can never redirect an expired link, even
   with clock skew between the app and Redis or if an expiration change failed to invalidate.
3. Otherwise SQL Server runs the same query as before (exact case, active, not expired). When it
   resolves, the entry is written with the TTL above, overwriting any stale entry.
4. Either way, `RedirectEndpoint` queues the click event, so **cache hits are counted like
   misses**. The id comes from the cached value.

Redis errors on `GET` or `SET` are logged as warnings and treated as a miss, or ignored for a
fill. The redirect then comes from SQL Server and never returns 500 because of Redis. While the
client is disconnected, `RedisRedirectCache` skips `GET`/`SET` instead of failing every request.
The outage is logged once by the `ConnectionFailed` / `ConnectionRestored` events.

## Invalidation (`ShortUrlManagementService`)

| Outcome | Cache action |
|---|---|
| PUT 200 | `DEL key` after the SQL commit |
| DELETE 204 | `DEL key` after the SQL commit; also on a **repeat** delete, which repairs an earlier failed invalidation |
| 400 / 404 / 409 | none (nothing was committed) |

The delete runs **after** the commit. Deleting before it would let a concurrent redirect
re-cache the old row before the new one is visible. POST never touches the cache: a new code
cannot have an entry, because non-resolving codes are never cached.

**A failed invalidation does not fail the request.** The SQL change is committed, so PUT still
returns 200 and DELETE 204. The failure is logged as an error naming the code. The rowversion
concurrency handling (409) is unchanged and is checked before any cache action.

## Consistency: eventual, bounded by `MaxTimeToLive`

This is **not** strictly consistent. After a successful PUT or DELETE, a redirect can still use
the previous destination, or still redirect a deleted link, in these cases:

| Case | How | Bound |
|---|---|---|
| Invalidation failed | Redis down or timing out during `DEL` | Entry's remaining TTL (≤ `MaxTimeToLive`) |
| Fill race | Redirect R misses and reads the old row, PUT/DELETE commits and runs `DEL`, then R runs `SET` with the old row | ≤ `MaxTimeToLive` |

Redis is shared, so with several API instances one `DEL` covers all of them. If Redis comes back
after a network partition with its data intact, entries whose `DEL` failed during the outage are
still stale until they expire.

The guarantees that do hold:

- **An expired link never redirects**, because the stored expiration is checked on every hit.
  This is independent of TTL and invalidation.
- Staleness is bounded by `MaxTimeToLive`, 5 minutes by default. Lower it to tighten the bound at
  the cost of more SQL lookups.

**Mitigation chosen:** a short, configurable maximum TTL, delete-after-commit, and the
repeat-DELETE repair. To remove a stale entry at once, repeat the DELETE, or re-send the PUT
(which runs `DEL` again even when nothing changes).

Rejected as too complex for this milestone:
- **Versioned entries** that compare the rowversion before `SET`. This needs a Lua script, or
  storing the version and checking it in SQL on every hit, which defeats the cache.
- **Delayed double-delete.** This needs a background scheduler.
- **Pub/sub or keyspace notifications.**
- **A durable outbox for invalidations.**

## Cache stampede and other trade-offs

- **Stampede:** when a hot entry expires, concurrent requests each miss and run the same
  single-row seek on `UX_UrlMappings_ShortCode`, then each `SET` the same value. That is cheap,
  so there is no lock or single-flight. Entries are created at different times, so expiries are
  naturally spread out.
- **Unknown codes are never cached**, so a scan of random codes always reaches SQL, though only
  as an index seek. Negative caching would be the fix, but it would need invalidation on POST
  and was ruled out by the requirement not to cache invalid links.
- **Extra latency on a miss:** one `GET` plus one `SET` round trip. The fill is awaited so that
  failures are logged, not lost.
- **Warning logs:** a slow, connected Redis that times out logs one warning per affected
  redirect.

## Tests

- `RedirectCacheTests`: TTL bounds, options validation, key format, value round trip and
  malformed values, and the forced connection options.
- `CachedShortCodeResolverTests`: hits skip SQL, misses fill with the right TTL, stale and
  expired hits defer to SQL, wrong-case and non-resolving codes are never cached, malformed codes
  never reach the cache, and get/set failures fall back to SQL.
- `ShortUrlManagementServiceTests`: invalidation on success and on repeat delete, none on
  400/404/409, failures still return success, and invalidation happens after the commit.
- `RedirectCacheHttpTests`: through the full pipeline, cache hits queue clicks, PUT/DELETE
  invalidate, a failed invalidation serves the stale entry and a repeat DELETE repairs it, Redis
  errors still return 302, and Redis is optional. One test uses the **real StackExchange.Redis
  client against an unreachable endpoint**: the app starts and GET/PUT/DELETE all work from SQL.
- Live Redis behaviour (real TTLs, keys and outages) is in `docs/integration-test-plan.md`
  (C1–C8).
