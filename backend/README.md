# AzureBank Backend

The backend of AzureBank: a .NET 10 REST API behind a Backend-For-Frontend (BFF), on SQL Server
through EF Core. The API owns accounts, deposits, withdrawals and transfers. The BFF is what the
browser talks to: it keeps the session, holds the JWT server-side and adds it to each call it
forwards. Which host enforces which security control is set out in
[`SECURITY.md`](../SECURITY.md).

## What is here

One solution, `AzureBank.slnx`.

| Project | What it is |
|---|---|
| [`src/AzureBank.Api`](src/AzureBank.Api/README.md) | The REST API: business logic, validation and authentication |
| [`src/AzureBank.Bff`](src/AzureBank.Bff/README.md) | The BFF gateway: session management, rate limiting, security headers and a YARP proxy to the API |
| [`src/AzureBank.Shared`](src/AzureBank.Shared/README.md) | Class library: domain entities, DTOs, exceptions and constants |
| [`src/AzureBank.Infrastructure`](src/AzureBank.Infrastructure/README.md) | Class library: the EF Core `DbContext`, migrations, data configurations and the shared notice relay |
| [`src/AzureBank.Functions.NoticeRelay`](src/AzureBank.Functions.NoticeRelay/README.md) | The notice relay as a timer-triggered Azure Function, rehearsed locally against Azurite (ADR-0051) |
| [`tests/AzureBank.Tests`](tests/AzureBank.Tests/README.md) | Unit, integration and architecture tests |
| `tests/AzureBank.Bff.Tests` | The BFF's integration tests |
| `tools/AzureBank.AuditVerifier` | Console tool over the audit trail: `verify`, `anchor`, `evidence`, `export` and `notify` |
| [`tools/AzureBank.Seeder`](tools/AzureBank.Seeder/README.md) | Console tool: `migrate`, `seed`, `reset`, and the demo pool's `seed-pool` and `recycle`; also what the tools image runs |

The API, the function and the two tools reference `AzureBank.Infrastructure` and
`AzureBank.Shared`. The BFF references `AzureBank.Shared` only: it has no access to the database.

`Directory.Build.props` holds what every project shares: `net10.0`, nullable reference types, and
warnings as errors in a Release build. `Directory.Packages.props` holds every NuGet version, and
a project file names a package without one (ADR-0004).

## Run it

It needs the .NET 10 SDK (`global.json`, at the repository root, names the version) and a SQL
Server.

The [local setup](../docs/engineering-practices.md#local-setup) is the one copy of the steps,
written from the repository root: the user-secrets each project reads, the one Seeder command
that drops, migrates and seeds the database, and the `dotnet run` line of each host.

With both hosts running:

- The API's documentation, served in Development only: <https://localhost:7215/scalar/v1>
- The BFF's session status: <http://localhost:5000/bff/auth/session-status>

## Test it

From `backend/`:

```bash
dotnet test AzureBank.slnx                                  # both test projects
dotnet test AzureBank.slnx --filter "Category=SqlServer"    # the tests that need SQL Server
dotnet test AzureBank.slnx --filter "Category!=SqlServer"   # everything except them
```

The tests that need a real database connect to the SQL Server that `AZUREBANK_TEST_SQLSERVER`
names, LocalDB locally and a service container in CI, and skip without it. They also skip when it
names an Azure SQL server: several of them create and drop databases there. `Category=SqlServer`
is the only `Category` trait the suite defines. The other commands, code coverage among them, are
in the [test project's README](tests/AzureBank.Tests/README.md).

## Configuration

Each host's `appsettings.json` holds its settings that are not secret. A secret comes from
`dotnet user-secrets` in development and from an environment variable elsewhere, spelled with
`__` in place of `:`; none is in a committed settings file.

- **Secrets.** The API needs a connection string and its secrets, and checks each at start:
  [Environment variables](src/AzureBank.Api/README.md#environment-variables). The Seeder needs
  only the connection string and the PIN pepper, and the BFF only `ServiceCredential__BffKey`,
  the same value the API holds.
- **The environment.** `dotnet run` sets Development for the API and the BFF, from each one's
  `launchSettings.json`; with nothing set a host runs as Production. `DOTNET_ENVIRONMENT`, when
  set, wins over `ASPNETCORE_ENVIRONMENT` (measured 2026-09-25). The console format (JSON in
  Production) and the BFF's HSTS (outside Development) follow the environment.
- **Waiting on the database (ADR-0058).** EF retries a transient failure 4 times with its
  back-off capped at 10 s, and a request still running after `RequestDeadline:Seconds` (40)
  answers 503 `SERVICE_UNAVAILABLE`. The BFF waits `BackendApi:TimeoutSeconds` (55) on the API, on
  its own client and on every proxied call, before it answers the same 503 itself: above the
  API's deadline and what the API may still need after it. The connection limits are code
  defaults and not in the file: `Database:ConnectTimeoutSeconds` (10),
  `Database:ConnectRetryCount` (0) and `Database:MaxPoolSize` (12; the Seeder's
  `appsettings.json` sets 5), plus `Pool Blocking Period=NeverBlock`, each written into the
  connection string only where the string leaves it unset, so a value in the string wins. The API
  checks every one at start and logs the limits it opened with. The API's README has
  [the ranges](src/AzureBank.Api/README.md#database-limits-and-the-request-deadline).
- **The BFF's own settings**, the session and the rate limits among them, are in
  [its README](src/AzureBank.Bff/README.md).

## Easy to get wrong

- **The API runs its `https` profile.** The BFF's proxy points at `https://localhost:7215`, and
  the API's `http` profile listens on port 5068 only.
- **Name the solution in `dotnet test`.** A filter on a test project's name leaves the BFF's
  tests out and still reports success ([engineering traps](../docs/engineering-traps.md)).
- **A secret two projects share is set in each of them.** The API, the BFF and the Seeder each
  have a user-secrets store of their own. The PIN pepper must be the same in the API and the
  Seeder, and the service credential the same in the API and the BFF.
- **The Seeder needs `DOTNET_ENVIRONMENT=Development`.** It has no launch profile, so a bare
  `dotnet run` runs it as Production, where its user-secrets do not load, and
  `ASPNETCORE_ENVIRONMENT` does not count there ([engineering traps](../docs/engineering-traps.md)).

## See also

- [How AzureBank works](../docs/architecture/overview.md): the architecture, one request end to
  end.
- [The OpenAPI contract](../docs/api/README.md): generated by the API and committed. The API's
  README lists its endpoints, and the BFF's its own.
- [The decision records](../docs/adr/README.md): why each part is the way it is.
- [Engineering practices](../docs/engineering-practices.md): local setup, quality gates, code
  style, merge policy.
