# Docker

Runs the API with SQL Server 2022 and Redis 7 in Docker Compose. Files: `Dockerfile`,
`.dockerignore`, `docker-compose.yml`, `.env.example`. Commands are for Windows PowerShell with
Docker Desktop (WSL2), run from the repository root.

The local development workflow (`dotnet run`, user-secrets, `dotnet ef`, `dotnet test`) is
unchanged and does not use these files.

## Architecture

| Service | Image | Host port | Purpose |
|---|---|---|---|
| `api` | `Dockerfile` target `api` | `127.0.0.1:8080` | The API on `aspnet:8.0`, non-root user `app`, HTTP on container port 8080 |
| `migrate` | `Dockerfile` target `migrator` | none | One-shot EF Core migration bundle; applies pending migrations, then exits |
| `sql` | `mcr.microsoft.com/mssql/server:2022-latest` | none | Data in named volume `urlshortener-stack_sql-data`; `mem_limit: 2g` |
| `redis` | `redis:7-alpine` | none | Redirect cache: `maxmemory 128mb`, `allkeys-lru`, no persistence; `mem_limit: 256m` |

Startup order, enforced by `depends_on`:

1. `sql` starts; its healthcheck runs `sqlcmd ... SELECT 1` every 10 s (30 s start period,
   10 retries), so nothing connects before SQL Server accepts logins.
2. `migrate` runs once `sql` is healthy. It creates the `UrlShortener` database if needed and
   applies pending migrations. It is idempotent and runs on every `up`
   ("No migrations were applied" when up to date).
3. `api` starts once `migrate` exited with code 0 and `redis` is healthy. Its healthcheck calls
   `/health/ready` (see [health-checks.md](health-checks.md)).

The API itself does not migrate at startup, so `dotnet run` behaves as before and several API
instances never race to migrate. If SQL Server goes down later, `/health/live` stays `200` and
`/health/ready` answers `503` within about 3 s; it recovers on its own once SQL is back.

The Compose project is named `urlshortener-stack`, so its containers
(`urlshortener-stack-api-1`, ...), network and volume never clash with the standalone
`urlshortener-sql` / `urlshortener-redis` containers or their `urlshortener-sql-data` volume.
SQL Server and Redis publish no host ports, so 1433 and 6379 stay free for those containers.

## Create `.env`

Compose reads secrets from `.env` in the repository root. It is git-ignored; never commit it.

```powershell
Copy-Item .env.example .env
notepad .env
```

- `MSSQL_SA_PASSWORD` (required): at least 8 characters with upper case, lower case, digits and
  a symbol. Use only letters, digits and `- _ ! . @`; `;` `"` `'` `$` `#` and spaces break the
  connection string or Compose interpolation.
- `ANALYTICS_VISITOR_HASH_SECRET` (optional): at least 32 characters, enables unique-visitor counts.
- `API_HOST_PORT` (optional, default `8080`), `PUBLIC_BASE_URL` (optional, default
  `http://localhost:<API_HOST_PORT>`), `ASPNETCORE_ENVIRONMENT` (optional, default `Production`;
  `Development` enables Swagger at `/swagger`).

To generate random values without displaying them:

```powershell
$pw  = "Us-" + [Guid]::NewGuid().ToString("N").Substring(0, 20) + "!"
$key = [Guid]::NewGuid().ToString("N") + [Guid]::NewGuid().ToString("N")
"MSSQL_SA_PASSWORD=$pw`nANALYTICS_VISITOR_HASH_SECRET=$key`nAPI_HOST_PORT=8080" |
    Set-Content -Encoding ascii .env
Remove-Variable pw, key
```

The SQL password is fixed when the `sql-data` volume is first created. Changing it in `.env`
later does not change the `sa` password in an existing volume (see Troubleshooting).

## Build and start

```powershell
docker compose build
docker compose up -d --wait        # returns once api is healthy (about 40 s on first start)
docker compose ps -a               # migrate shows "Exited (0)"; the others "(healthy)"
```

## Health checks and a smoke test

```powershell
Invoke-RestMethod http://localhost:8080/health/live
Invoke-RestMethod http://localhost:8080/health/ready   # sql and redis should be Healthy

$r = Invoke-RestMethod -Method Post http://localhost:8080/api/v1/shorten `
    -ContentType 'application/json' -Body '{"originalUrl":"https://example.com/docs"}'
$r.shortUrl
curl.exe -s -o NUL -w "%{http_code} %{redirect_url}`n" $r.shortUrl   # 302 https://example.com/docs
Invoke-RestMethod "http://localhost:8080/api/v1/analytics/$($r.shortCode)"
```

Use `curl.exe`, not `curl`: in Windows PowerShell `curl` is an alias for `Invoke-WebRequest`.

## Logs

```powershell
docker compose logs -f api
docker compose logs migrate
docker compose logs --tail 50 sql
```

## Shut down

```powershell
docker compose stop        # stop containers, keep them
docker compose down        # remove containers and network; the SQL data volume is kept
```

`docker compose down -v` also deletes `urlshortener-stack_sql-data` and all data in it. Only use
it to deliberately reset the database.

## Rebuild after code changes

```powershell
docker compose up -d --build --wait
```

A new migration is applied by `migrate` on the next `up` (the bundle is rebuilt with the image).

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `required variable MSSQL_SA_PASSWORD is missing a value` | `.env` is missing or the value is empty; see [Create `.env`](#create-env). |
| `sql` never becomes healthy; logs say the password does not meet policy | Use a longer password with upper, lower, digit and symbol. Remove the volume only if it holds no data you need. |
| `sql` exits with code 137 or "requires at least 2000 megabytes" | Docker Desktop is short of memory. Stop the standalone `urlshortener-sql` container while using the stack (`docker stop urlshortener-sql`), or raise the WSL2 memory in `%UserProfile%\.wslconfig`. |
| `migrate` exits non-zero: `Login failed for user 'sa'` | `.env` password differs from the one the volume was created with. Restore the original value in `.env`. |
| `api` not started: `dependency failed to start` | Check `docker compose logs migrate` and `docker compose logs sql`. |
| `/health/ready` is `503` | SQL Server unreachable: `docker compose ps sql`, `docker compose logs sql`. Redis down only gives `Degraded` (still `200`). |
| `bind: address already in use` on 8080 | Set `API_HOST_PORT=8081` in `.env`; short URLs follow it unless `PUBLIC_BASE_URL` is set. |
| Swagger returns 404 | Swagger is only on in Development: set `ASPNETCORE_ENVIRONMENT=Development` in `.env`. |
| Log warning "Failed to determine the https port for redirect" | Expected: the container serves plain HTTP only, so HTTPS redirection is skipped. |

To query the database from inside the network (the password is expanded inside the container,
never on the host). Windows PowerShell 5.1 strips `"` inside arguments to native commands, so the
query uses doubled single quotes instead:

```powershell
docker compose exec sql bash -c '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $MSSQL_SA_PASSWORD -d UrlShortener -Q ''SELECT COUNT(*) FROM UrlMappings'''
```

## Security notes and limitations

- Local/assessment use only. The API connects as `sa`; a real deployment would use a
  least-privilege login, a secret store instead of `.env`, and TLS at an ingress.
- `docker compose config` and `docker inspect` on `api`/`migrate` print the connection string
  with the password. Do not paste their output anywhere.
- Redis has no password; it is reachable only on the Compose network.
- SQL Server uses its self-signed certificate (`TrustServerCertificate=True`) on the internal network.
- `2022-latest` and `7-alpine` are moving tags; pin digests for reproducible builds.
- The image is built for the host's architecture (linux/amd64 on typical Windows machines).
