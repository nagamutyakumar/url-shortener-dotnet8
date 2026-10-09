# AI collaboration log

This document describes how AI assistance was used to build this project and how its output was
checked. Its purpose is to let a reviewer judge the work with that context in mind.

## Summary

- **Tool:** Claude Code (Anthropic's command-line coding assistant) was used as an
  implementation and review assistant throughout the project.
- **AI role:** Claude Code proposed designs, wrote most of the application code, tests and
  documentation in this repository, and reviewed its own and earlier changes for correctness.
- **Human role (project owner's account):** The project owner set the scope of each milestone
  and reviewed the proposed architecture and diffs. They ran or approved commands in their local
  environment, validated API behaviour, investigated failures, and decided what was committed.
  Git shows that every commit was authored by the project owner.
- **Authorship:** The code should be read as AI-generated and human-reviewed. It was not
  written by hand independently of the assistant, and this document does not claim it was.

## Sources and their limits

This log is reconstructed from evidence in the repository:

- `git log` (16 commits through `eba943b`, 2026-10-07 to 2026-10-09);
- the design and validation notes in `docs/` written with each milestone;
- comments in the source and test code;
- for Milestone 11 itself, the commands run while writing this document.

The repository does **not** record:

- **Which lines came from the AI.** The commits carry no AI co-author trailers, so git metadata
  alone cannot separate AI-written lines from human edits.
- **The prompts or conversation transcripts.** No prompts are quoted or paraphrased here.
- **Time spent, token usage or the number of iterations.** None of these are estimated.
- **Who ran each check.** Where `docs/` records a live measurement, this log reports the
  measurement but does not say who took it.

## Workflow

The project was built in small milestones, normally one per commit. The loop below is the
project owner's description of the workflow. The repository does not record the individual
steps.

1. **Scope.** The project owner defined the milestone: one feature or concern, with its
   constraints.
2. **Inspect and propose.** Claude Code read the existing code and docs, then proposed an
   approach, including the alternatives it rejected.
3. **Implement.** Claude Code wrote the production code, the unit and HTTP-level tests, and a
   design note in `docs/` covering behaviour, trade-offs and limitations.
4. **Verify.** The change was built and tested with `dotnet build` and `dotnet test`. Behaviour
   that the in-process tests cannot show (SQL Server collations, live Redis, container startup,
   outages) was checked manually against local containers in some cases, such as the SQL
   health-check measurement in example 2. Otherwise it was written down as a scenario in
   `docs/integration-test-plan.md`.
5. **Review and commit.** The project owner reviewed the diff, asked for corrections where
   needed, and committed it.

Two features of the history are consistent with this loop:

- From `ca8bd9e` to `52e4f36`, each commit contains application code, tests and documentation
  together. The Docker and CI commits add configuration and documentation but no tests.
- Later milestones update earlier documents when behaviour changes. For example, the caching
  commit amended `analytics.md` and `management.md`, and the health-check commit amended
  `rate-limiting.md`.

## Milestone timeline (from Git history)

| Commit | Date | Commit subject | Tests / docs added |
|---|---|---|---|
| `68a75e6` | 2026-10-07 | Initialize .NET 8 Web API project | – |
| `efab93a` | 2026-10-07 | Add xUnit test project | placeholder test |
| `d9ba4c9` | 2026-10-07 | Fix test project reference and API request template | adds the missing project reference; removes the template's `weatherforecast` request |
| `141033b` | 2026-10-07 | Add secure Base62 short code generator | generator and format tests |
| `da5086e` | 2026-10-07 | Add EF Core SQL Server persistence foundation | model and UTC converter tests |
| `354d0c7` | 2026-10-08 | Add initial SQL Server database migration | – |
| `ca8bd9e` | 2026-10-08 | Implement URL shortening endpoint with validation and deduplication | service, canonicaliser and HTTP tests; `integration-test-plan.md` |
| `c8a4ece` | 2026-10-08 | Implement short URL redirects with expiration checks | resolver and HTTP tests; `ApiFactory` |
| `0303d11` | 2026-10-08 | Add background click tracking and analytics endpoint | queue, writer, hasher, analytics and SQL-translation tests; `analytics.md` |
| `0a89368` | 2026-10-09 | Implement URL management with soft delete and concurrency handling | service and HTTP tests; `management.md` |
| `65d6a54` | 2026-10-09 | Add Redis redirect caching with invalidation and SQL fallback | cache, resolver and HTTP tests; `caching.md` |
| `a6321ff` | 2026-10-09 | Add configurable API rate limiting with per-client policies | options and HTTP tests; `rate-limiting.md` |
| `52e4f36` | 2026-10-09 | Add SQL Server and Redis health checks with bounded timeouts | health tests; `health-checks.md` |
| `3ab36ff` | 2026-10-09 | Add Docker Compose deployment with SQL migrations and Redis | `docker.md` |
| `03f2d9d` | 2026-10-09 | Add GitHub Actions CI for .NET 8 build and tests | `ci.md` |
| `eba943b` | 2026-10-09 | Add project README and AI collaboration documentation | `README.md`, this document; corrections to five existing docs |

## Division of responsibility

| Activity | Claude Code | Project owner |
|---|---|---|
| Architecture and design options | Proposed designs and documented the alternatives it rejected (for example `IDistributedCache`, the Channel drop modes, versioned cache entries) | Reviewed and accepted or redirected them |
| Production code and tests | Generated them | Reviewed diffs and requested changes |
| Running commands | Ran build and test commands when permitted (for example, the test run recorded below) | Ran or approved commands in the local environment, including Docker and SQL Server |
| API behaviour | Wrote automated HTTP-level tests and `.http` examples | Validated behaviour against the running API and local containers |
| Failure investigation | Analysed results and proposed fixes | Supplied observations from the real environment and judged the fix |
| Documentation | Drafted the `docs/` notes and this log | Reviewed them for accuracy |
| Commits | Did not commit | Made and approved every commit |

The project owner column records the owner's account of the workflow. Of these activities, the
repository directly shows only the commits (all authored by the owner) and the live
measurements written down in `docs/`.

## Worked examples

### 1. Redis test isolation (`65d6a54`)

**Problem.** Adding Redis made the cache depend on configuration (`ConnectionStrings:Redis`). A
developer who had set that value in user-secrets or an environment variable for manual testing
would otherwise have had it picked up by the in-process test host. Test results would then
depend on that machine's settings and on whether a Redis server happened to be running.

**What was implemented.**
- `TestDoubles/ApiFactory.cs` sets `ConnectionStrings:Redis` to an empty string, so every HTTP
  test runs with `NullRedirectCache` unless the test opts in.
- Cache behaviour is tested through `FakeRedirectCache`.
- One test points the real StackExchange.Redis client at an unreachable loopback endpoint
  (`127.0.0.1:1`). It confirms that the app still starts and that GET, PUT and DELETE are served
  from SQL. The health-check tests use the same kind of endpoint to cover the `Degraded` status.
- The rate-limiting milestone (`a6321ff`) used the same pattern for limits: high limits by
  default, with a separate limiter state for each factory.
- The health-check milestone (`52e4f36`) extracted `IsRedisConfigured` so that the `redis`
  readiness check and the cache use the same switch. Without Redis, tests see no `redis` entry.

**Verification.** `docs/ci.md` states that the suite needs no SQL Server, Redis, Docker or
user-secrets. The CI workflow relies on this. Live Redis behaviour, such as real TTLs, `DEL` on
update and outage recovery, is listed as manual scenarios C1–C8 in `integration-test-plan.md`.
The in-process tests do not cover it.

**Responsibility.** This change was made in the AI-assisted workflow described above. The
repository does not record who identified the problem or who wrote particular lines, so neither
is claimed.

### 2. SQL health-check timeout investigation (`52e4f36`)

**Problem.** `docs/health-checks.md` records that the first version of the SQL check called
`DbContext.Database.CanConnectAsync`. In a live test with the SQL Server container stopped,
readiness correctly returned `503`, but only after **14.7 s** (`sql` duration 14,724 ms)
instead of the configured 3 s.

**Investigation recorded in the docs.**
- The application connection string sets no `Connect Timeout`, so SqlClient falls back to its
  15 s default, which matches the observed delay.
- In isolated tests, SqlClient did honour the cancellation token on a fresh pool. The docs
  therefore suggest the open went through the shared pool's path to the stopped server. This is
  a hypothesis, not a proven root cause: it could not be reproduced without stopping SQL Server.
- An experiment showed that `DefaultHealthCheckService` does not enforce a timeout on a check
  that ignores its token: a check that slept 2 s with a 100 ms timeout took 2,004 ms.
- EF Core retries were ruled out, since `EnableRetryOnFailure` is not configured.

**Fix.** The fix does not depend on the unconfirmed cause. `SqlServerHealthCheck` opens its own
non-pooled connection with a 2 s connect timeout, a 2 s command timeout and
`ConnectRetryCount=0`, and wraps the probe in `WaitAsync`. The
application's `DbContext` connection settings were left unchanged. `SqlServerHealthCheckTests`
includes a timeout test against a loopback `TcpListener` that accepts connections and never
responds.

**Responsibility.** The live measurement required stopping a local SQL Server container. The
repository does not record who ran it. The analysis, fix and tests were produced in the
AI-assisted workflow. By the project owner's account, choosing a fix that leaves application
behaviour unchanged, and accepting that the cause could not be confirmed, were review decisions.

### 3. Docker Compose migration design (`3ab36ff`)

**Decision.** Schema migration runs as a separate one-shot `migrate` service, not inside the API
at startup:

- The `Dockerfile` has a `migrator` target that builds an EF Core migration bundle with
  `dotnet-ef` pinned to 8.0.31, the same version as the EF packages.
- Building the bundle starts the app's host, and `Program` refuses to start without a connection
  string. The build therefore passes a placeholder connection string that has no credentials and
  is never connected to. The real connection string is supplied at run time.
- Startup order is enforced with `depends_on`: `sql` must be healthy before `migrate` runs, and
  `migrate` must have exited successfully (`service_completed_successfully`) before `api` starts.
  The bundle is idempotent and runs on every `up`.

**Why this approach.** `docs/docker.md` gives two reasons: `dotnet run` keeps its existing
behaviour, and multiple API instances can never race to apply migrations. The Compose project
name, the absence of SQL/Redis host ports and the `$$`-deferred password in the SQL healthcheck
all follow the same goal: the stack must not clash with the developer's existing standalone
containers or expose the password.

**Verification recorded.** `docs/docker.md` describes the expected `docker compose ps -a` state
(`migrate` "Exited (0)", the other services healthy), the "No migrations were applied" message
on later runs, a smoke test (shorten, then `302`, then analytics), and a troubleshooting table.
The symptoms in that table include SQL Server exit code 137 under memory pressure and
`Login failed for user 'sa'` after a password change. The repository does not record test logs
from these runs.

**Responsibility.** The Dockerfile, Compose file and guide were produced in the AI-assisted
workflow. Running the stack requires Docker Desktop, the developer's `.env` file and the local
machine's resources. By the project owner's account, they ran and checked the containers.

### 4. CI validation (`03f2d9d`)

**What was implemented.** A single GitHub Actions job (`.github/workflows/ci.yml`) restores,
builds in Release and runs the tests, with:

- `contents: read` permissions and no secrets;
- a 15-minute timeout;
- cancellation of superseded pull-request runs, but never of runs on `main`;
- a `.trx` artifact uploaded only on failure.

**Why it needs no infrastructure.** The isolation from example 1 is what allows this. Tests
replace SQL Server with InMemory and clear Redis. The only network connections are to loopback
addresses: a `TcpListener` in the SQL timeout test, and the unreachable `127.0.0.1:1` Redis
endpoint.

**Validation.**
- `docs/ci.md` lists the same commands for running locally.
- During Milestone 11, Claude Code ran `dotnet test UrlShortener.sln --configuration Release` in
  this working copy. Result: **462 passed, 0 failed, 0 skipped**.
- This local clone has no Git remote configured, so the repository itself does not show whether
  the workflow has run on GitHub. No GitHub Actions run duration is recorded, and `docs/ci.md`
  does not state one.

## AI-generated content: what was checked and what was not

**Checked:**
- Every commit from `141033b` onward that changes application code includes automated tests,
  and the full suite passes (462 tests at the time of writing).
- Each design note records its trade-offs and limitations, so behaviour that was deliberately
  left out is stated rather than hidden. Examples: lossy click delivery, eventual cache
  consistency, per-instance rate limits, and the absence of authentication.

**Not checked automatically:**
- The SQL Server-specific behaviour listed in `docs/integration-test-plan.md`: collations, the
  unique index, `rowversion`, `datetime2(3)` rounding and transactions. These have no automated
  tests. They are documented as manual or future integration scenarios.
- Some explanations in `docs/` were written as analysis and not reproduced, such as the
  connection-pool hypothesis in example 2.

Reviewers should therefore treat the integration-test plan as untested scope, not as evidence
of coverage.

## Milestone 11 notes

The README and this log were drafted by Claude Code from the repository contents. Before
drafting, it read the source, tests, configuration, Docker files, workflow, all `docs/` files
and the full Git history. A follow-up accuracy pass corrected five statements in existing docs
that did not match the code: `integration-test-plan.md`, `health-checks.md`, `analytics.md`,
`rate-limiting.md` and `ci.md`. No application code, tests, configuration or dependencies were
changed. The project owner reviewed the result and committed it as `eba943b`.
