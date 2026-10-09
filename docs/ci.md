# Continuous integration

The workflow lives in `.github/workflows/ci.yml`. It runs on every push to `main` and
on every pull request that targets `main`.

## What it does

One job, `Build and test`, runs on `ubuntu-latest` with the .NET 8 SDK:

1. **Restore** – `dotnet restore UrlShortener.sln`
2. **Build** – `dotnet build --configuration Release --no-restore`
3. **Test** – `dotnet test --configuration Release --no-build`, which runs the full xUnit
   suite against the binaries from step 2
4. **Upload test results**: only when a step fails, the `.trx` file is uploaded as the
   `test-results` artifact and kept for 7 days

The restore and build each run once. `--no-restore` and `--no-build` stop the later
steps from repeating them.

Other settings:

- **Permissions:** `contents: read` only. The workflow does not deploy, publish or use
  any secrets.
- **Timeout:** the job is cancelled after 15 minutes. A normal run takes 1–2 minutes.
- **Concurrency:** when you push again to a pull request, its earlier run is cancelled.
  Runs on `main` are never cancelled, so every commit on `main` gets a result.

## No external infrastructure

The tests don't need SQL Server, Redis, Docker, user-secrets or credentials:

- `TestDoubles/ApiFactory` hosts the real `Program` pipeline with the database replaced
  by EF Core InMemory. It also sets `ConnectionStrings:Redis` to empty and uses a
  placeholder SQL connection string that is never opened. Because of this, a
  developer's user-secrets or `ConnectionStrings__*` environment variables can't reach
  the tests.
- Tests that use `UseSqlServer("Server=unused…")` only build the EF model or translate
  queries to SQL. They never connect.
- The SQL health-check timeout test runs its own `TcpListener` on loopback with an
  ephemeral port.

## Reading a failure

- **Restore failed:** a NuGet package or version can't be found, or nuget.org is
  unavailable. Check any package version you recently changed.
- **Build failed:** compiler errors appear in the step log and as annotations on the
  run summary and the PR's *Files changed* tab. `setup-dotnet` registers a problem
  matcher for the C# compiler, which also shows **build warnings** as annotations.
  Warnings don't fail the build.
- **Test failed:** the console logger prints every test as `Passed`/`Failed`. Each
  failure includes its assertion message and stack trace, followed by a
  `Total / Passed / Failed` summary. Search the `Test` step log for `Failed `. You can
  also download the `test-results` artifact and open the `.trx` file in Visual Studio
  or another TRX viewer.
- **Cancelled:** a newer push to the same PR superseded this run, or the run hit the
  15-minute timeout. A timeout usually means a test is hanging.

## Running the same checks locally

From the repository root, with the .NET 8 SDK installed:

```bash
dotnet restore UrlShortener.sln
dotnet build UrlShortener.sln --configuration Release --no-restore
dotnet test UrlShortener.sln --configuration Release --no-build --logger "console;verbosity=normal"
```

None of these commands need Docker, SQL Server or Redis to be running.

## Out of scope

This workflow is CI only. It doesn't deploy, publish container images or use cloud
credentials. There is no NuGet package cache, because the repository has no
`packages.lock.json` to key a cache on, and a cold restore of this solution is quick.
