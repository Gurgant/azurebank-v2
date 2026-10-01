# AzureBank.Seeder

A console tool with three commands: `migrate` brings the database to the latest migration, `seed`
adds the demo data, `reset` drops the database and does both. It is also what the tools image
runs: the one-shot containers that prepare the database before the API starts
([ADR-0060](../../../docs/adr/0060-migrations-run-as-a-one-shot-container-before-the-app.md)).

This file is the tool's contract: what a job that runs it can rely on. A new command is added
here.

## The image

```bash
docker build -f backend/tools/AzureBank.Seeder/Dockerfile -t azurebank-tools .   # from the repository root
```

`compose.yaml` builds it as `<project>-tools` (`azurebank-tools` by default). It carries the label
`org.opencontainers.image.source`, runs as uid 1654, writes no file and listens on nothing.

Its entrypoint is `dotnet azurebank-seeder.dll`, so **a container's arguments are the command**:
`["migrate"]`, `["seed"]`. With compose, arguments after the service name replace the service's
command, so an option needs the command again: `docker compose run --rm migrate migrate
--wait-seconds 120`.

**Configuration comes from the environment only.** An argument is never read as a setting, and the
parser prints back an argument it does not know, so no secret goes in the arguments. With no
`DOTNET_ENVIRONMENT` the tool runs as Production. The settings file beside the binary holds the
pool size, the log levels and the demo data's public password and PIN; it holds no connection
string.

## Commands

| Command | Does | Needs | Runs |
|---|---|---|---|
| `migrate [--wait-seconds N]` | Waits up to N seconds (60; 0 to 600) for the database to accept a connection, then applies every pending migration; changes nothing when there is none. Off Azure it creates the database if the server has none by that name; on an Azure SQL server name it never creates one | The connection string, with a login that may change the schema. **No pepper, no other secret** | Everywhere, a deployment included |
| `seed` | Adds the two roles, the four demo users with their public password and PIN, their five accounts and a 26-row ledger, to a database that has none of them | The connection string and the PIN pepper, which must be the API's | Local runs and CI. **Refuses an Azure SQL server name** |
| `reset [--confirm]` | Drops the database, migrates, seeds | The same as `seed` | Development and CI. **Refuses an Azure SQL server name** |

## Variables

| Variable | For | |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | every command | Required; the name the API reads. A password that holds `;`, `=` or a quote has to be quoted, or the string cannot be read |
| `Security__PinPepper` | `seed`, `reset` | 32 characters or more, equal to the API's (ADR-0011) |
| `Database__MaxRetryCount`, `Database__MaxRetryDelay`, `Database__ConnectTimeoutSeconds`, `Database__ConnectRetryCount`, `Database__MaxPoolSize` | every command | Optional. 4, `00:00:10`, 10, 0 and 5 (ADR-0058; the pool of 5 is this tool's own). A keyword in the connection string wins |

In Development (`DOTNET_ENVIRONMENT=Development`) the two required values come from the project's
user-secrets instead (`docs/engineering-practices.md`, "Local setup").

## Exit codes

| Exit | Means |
|---|---|
| 0 | Done. Running it again is safe and changes nothing |
| 1 | Failed after the server was contacted, or cancelled, or the command line was wrong. Running it again is safe. It helps when the cause passes: the database was not reachable in time, another run held the lock, the run was stopped. It does not help until something else changes for: a refused login; a database this login cannot open; a database that is ahead of the build; a missing database on Azure SQL; a model change with no migration; an incomplete seed (run `reset`) |
| 2 | Refused before any connection was opened: a required variable is missing, too short or unreadable, or `seed` or `reset` was aimed at an Azure SQL server name |

## What a run prints

Text, on standard output, one event per line. `migrate` prints lines like these (a run that
waited for the server and then migrated an empty database):

```text
[18:35:05 INF] Database limits: connect timeout 10 s, connect retries 0, pool 5, pool blocking NeverBlock; EF retries 4, back-off capped at 00:00:10
[18:35:38 WRN] Waiting for the database (4060, class 11): Cannot open database "AzureBank" requested by the login. The login failed. 27 s left.
[18:35:42 INF] Pending migrations: 16, from 20260112054539_InitialCreate to 20260928192931_AddUserSessionStamp
[18:35:42 INF] Applying migration '20260112054539_InitialCreate'.
[18:35:44 INF] The database is at 20260928192931_AddUserSessionStamp: 16 of 16 migrations. migrate took 39.7 s
```

- `Waiting for the database` is a Warning, once for every attempt that failed, with SQL Server's
  error number and class and the first line of its message. That line can name the server, the
  database and the login; SqlClient puts no password in it.
- `Applying migration` is EF's own line, once for each migration this run applied. `migrate` itself
  claims no count: two runs at once read the same pending list.
- A refusal or a failure is one Error line that starts with the command's name: `migrate refused:
  …`, `seed failed: …`. EF may log Errors of its own before it.

## Things to know

- **Two `migrate` runs at once are safe.** On a migrated database both exit 0. The second waits
  for the first, which holds EF's migration lock; past the 30 s command timeout it exits 1 and can
  be run again. On a local server without the database, one creates it and the other exits 1.
- **A database ahead of the build is refused**, exit 1, and nothing is changed: an older build must
  not move an app onto a schema it does not know. Locally that is a volume a newer branch
  migrated: `docker compose down -v`.
- **SIGTERM cancels the run and the exit code is 1.** The last line says whether running it again
  is safe. A `migrate` that was stopped is: only migrations the history table does not list are
  applied.
- **`migrate` leaves a schema with no rows**: no roles, no users. The roles come from `seed`. Where
  `seed` never runs, whatever creates the first user has to create the roles.
- **`seed` fills an empty database only.** The seeders skip what is already there, so a seed that
  was cut short is not completed by running it again: it exits 1 every time, and `reset` starts
  over.
- **The Azure SQL rule goes by the server's name**: `.database.windows.net`,
  `.database.cloudapi.de`, `.database.usgovcloudapi.net`, `.database.chinacloudapi.cn` and
  `.database.fabric.microsoft.com`, the five SqlClient itself knows. An alias or an IP address in
  front of an Azure SQL server is not recognised.
- **A declined `reset` prompt exits 0.** Nothing was done and nothing failed. In a container there
  is no terminal: pass `--confirm`.
