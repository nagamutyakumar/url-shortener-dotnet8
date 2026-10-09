# Click analytics

## Collection (`GET /{code}`)

Each successful 302 queues exactly one click; 404s (malformed, unknown, wrong-case,
inactive, expired) queue nothing. The redirect never waits for the database. Redirects served
from the Redis cache (`caching.md`) queue their click the same way, with the mapping id from the
cached entry.

```
RedirectEndpoint ─► ClickTracker.Track ─► ClickEventQueue (bounded Channel) ─► ClickEventWriter (BackgroundService) ─► ClickEvents
                      (never throws)        TryWrite only, never blocks          fresh DI scope + DbContext per batch
```

| Setting (`Analytics:`) | Default | Notes |
|---|---|---|
| `QueueCapacity` | 10000 | 1–1,000,000. Memory bound for buffered clicks. |
| `BatchSize` | 100 | 1–1,000 rows per `SaveChanges`. |
| `VisitorHashSecret` | unset | ≥ 32 characters. Supply via user-secrets / `Analytics__VisitorHashSecret`; never commit it. |

### Queue-full policy: drop the new event and count it

The channel uses `BoundedChannelFullMode.Wait`, but redirects only call `TryWrite`, which in that
mode returns `false` instead of waiting. The click is dropped, `ClickEventQueue.DroppedCount` is
incremented, and the writer logs the dropped total as a warning (not once per event, which would
flood the logs under load). The `DropNewest`/`DropOldest`/`DropWrite` modes were rejected because
their `TryWrite` reports success while discarding, so drops could not be counted.

### Delivery is best-effort, not guaranteed

Clicks are lost, and the redirect still succeeds, when:

| Cause | Visible as |
|---|---|
| Queue full | Warning with dropped count |
| Enqueue throws | Warning from `ClickTracker` |
| Batch insert fails (DB down, timeout, constraint) | Error with batch size; the batch is discarded, not retried, so an outage cannot build an unbounded backlog |
| Shutdown cancels an in-flight insert | Warning with batch size |
| Shutdown flush exceeds 5 s | Warning with unwritten count |
| Process crash / kill | Not observable: buffered clicks are in memory only |

Every exception in the writer is caught per batch. An unhandled one would stop the host, because
.NET 8's default `BackgroundServiceExceptionBehavior` is `StopHost`. Durable delivery would need
an outbox or an external broker, which is out of scope here.

Clicks are also counted for bots and link-preview fetchers, since nothing filters them.

### Visitor hash

`VisitorHash = HMAC-SHA256(secret, "{UrlMappingId}\n{remote IP}\n{User-Agent}")`, computed during
the request. The raw IP address and User-Agent are never queued, logged or stored.

- **Per link:** the mapping id is part of the input, so one visitor's hashes on different links
  cannot be linked to each other.
- **Null when:** no secret is configured, or the connection has no remote IP (for example under
  TestServer). A User-Agent alone is too coarse to identify a visitor.
- **Approximate:** visitors who share an IP address and browser (NAT, corporate proxies, mobile
  carriers) merge into one; a visitor whose IP or browser changes is counted again.
- **Proxies:** `RemoteIpAddress` is the TCP peer. `X-Forwarded-For` is deliberately not honoured
  (as with `ShortUrlOptions`), so behind a reverse proxy every visitor has the proxy's address.
  Configure `ForwardedHeaders` with `KnownProxies` before relying on the numbers there.
- **Secret handling:** anyone holding the secret can test guesses (the whole IPv4 space × common
  user agents), so protect it like a credential. Rotating it resets visitor identity: returning
  visitors look new.

### Country

`CountryCode` is always stored as `null`. No geolocation provider is configured, and a country is
never inferred from the IP address. A trusted source (for example a CDN header that the edge
proxy sets and overwrites) would be read in `ClickTracker`.

`Browser` is also left `null`. Analytics does not report it, so it is not derived.

## Analytics API (`GET /api/v1/analytics/{code}`)

```json
{
  "shortCode": "Abc1234",
  "linkStatus": "active",
  "expiresAtUtc": null,
  "totalClicks": 3,
  "uniqueVisitors": { "status": "partial", "count": 1, "clicksWithVisitorHash": 1 },
  "countries": [ { "countryCode": "DE", "clicks": 1 } ],
  "clicksWithUnknownCountry": 2
}
```

- **404** (generic problem details, same as redirects): malformed code, unknown code, or a code
  that differs only in case. Matching is case-sensitive.
- **200 for inactive and expired links**, with `linkStatus` set to `inactive` or `expired`.
  Click history is deliberately retained (`ClickEvents → UrlMappings` uses `Restrict`), so it
  stays readable after a link stops redirecting. `DELETE /api/v1/shorten/{code}` is what makes a
  link inactive, and PUT keeps the mapping id, so history survives both (see `management.md`). There is no authentication, so, like every
  endpoint, this reveals that a code exists.
- **`uniqueVisitors.status`** separates "unknown" from "zero":
  - `complete`: every click has a hash. `count` is a real count, including `0` for no clicks.
  - `partial`: only `clicksWithVisitorHash` of the clicks have a hash, so `count` covers only
    those clicks.
  - `unavailable`: there are clicks but none has a hash. `count` is `null`, never `0`.
- **`countries`** lists only stored codes, most clicks first. `clicksWithUnknownCountry` covers
  the rest; today that is every click.
- **Freshness:** the totals include only persisted clicks. The newest clicks may still be
  queued, and lost clicks are never included.
- **Queries:** one `COUNT_BIG` / `COUNT_BIG(DISTINCT VisitorHash)` aggregate plus one
  `GROUP BY CountryCode`, both filtered by `UrlMappingId`, which seeks
  `IX_ClickEvents_UrlMappingId_ClickedAtUtc`. The two queries do not share a snapshot, so a click
  written between them can make the country numbers differ slightly from the totals. With very
  high click volumes, a covering index (`INCLUDE (VisitorHash, CountryCode)`) would remove the
  key lookups. That is a schema change and was not made.
