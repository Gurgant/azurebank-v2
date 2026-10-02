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
`org.opencontainers.image.source`, runs as uid 1654, cannot write to its own folder and listens on
nothing.

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
| `migrate [--wait-seconds N]` | Waits up to N seconds (60; 0 to 600) for the database to accept a connection, then applies every pending migration; changes nothing when there is none. Off Azure it creates the database if the server has none by that name. On an Azure SQL server name it never creates one, and it goes on only once an open has succeeded | The connection string, with a login that may change the schema. **No pepper, no other secret** | Everywhere, a deployment included |
| `seed` | Adds the two roles, the four demo users with their public password and PIN, their five accounts and a 26-row ledger, to a database that has none of them. A database that was seeded is left as it is, exit 0, whatever its users did since | The connection string and the PIN pepper, which must be the API's | Local runs and CI. **Refuses an Azure SQL server name** |
| `reset [--confirm]` | Drops the database, migrates, seeds | The same as `seed` | Development and CI. **Refuses an Azure SQL server name** |

## Variables

| Variable | For | |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | every command | Required; the name the API reads. It has to name a server and a database, and it says how to sign in (below). A password that holds `;`, `=` or a quote has to be quoted, or the string cannot be read |
| `Security__PinPepper` | `seed`, `reset` | 32 characters or more, equal to the API's (ADR-0011) |
| `Database__MaxRetryCount`, `Database__MaxRetryDelay`, `Database__ConnectTimeoutSeconds`, `Database__ConnectRetryCount`, `Database__MaxPoolSize` | every command | Optional. 4, `00:00:10`, 10, 0 and 5 (ADR-0058; the pool of 5 is this tool's own). A keyword in the connection string wins. `migrate` refuses a connect timeout of 0, which means no limit, from either place |

In Development (`DOTNET_ENVIRONMENT=Development`) the two required values come from the project's
user-secrets instead (`docs/engineering-practices.md`, "Local setup").

**How the string signs in.** Either with a user and a password, as `compose.yaml` and CI do, or
with a managed identity and no password:
`Authentication=Active Directory Managed Identity;User ID=<the identity's client ID>`. The Azure
deployment uses the second, with one identity for the app and another for `migrate`
([infra/README.md](../../../infra/README.md)). The tool has no setting for it: it reads such a
string, keeps the sign-in when it fills in the limits, and SqlClient asks Azure for the token. A
managed identity exists only on Azure, so no local run and no CI job signs in that way. Where this
file says "login", read: whatever the string signs in as.

## Exit codes

| Exit | Means |
|---|---|
| 0 | Done. Running it again is safe and changes nothing |
| 1 | Failed after the server was contacted, or cancelled, or the command line was wrong. Running it again is safe. It helps when the cause passes: the database was not reachable in time, another run held the lock, the run was stopped. It does not help until something else changes for: a refused login; a database this login cannot open; a database that is ahead of the build; a missing database on Azure SQL; a model change with no migration; an incomplete seed (run `reset`) |
| 2 | Refused before any connection was opened: a required variable is missing, too short or unreadable, the connection string names no database, `migrate` was given a connect timeout of 0, or `seed` or `reset` was aimed at an Azure SQL server name |

## What a run prints

Text, on standard output. An event is one line, except an Error that reports an exception: the
exception's stack follows it. `migrate` prints lines like these (a run that waited for the server
and then migrated an empty database):

```text
[18:35:05 INF] Database limits: connect timeout 10 s, connect retries 0, pool 5, pool blocking NeverBlock; EF retries 4, back-off capped at 00:00:10
[18:35:38 WRN] Waiting for the database (4060, class 11): Cannot open database "AzureBank" requested by the login. The login failed. 27 s left.
[18:35:42 INF] Pending migrations: 16, from 20260112054539_InitialCreate to 20260928192931_AddUserSessionStamp
[18:35:42 INF] Applying migration '20260112054539_InitialCreate'.
[18:35:44 INF] The database is at 20260928192931_AddUserSessionStamp: 16 of 16 migrations. migrate took 39.7 s
```

- `Waiting for the database` is a Warning, once for every attempt that failed, with SQL Server's
  error number and class and the first line of its message. That line can name the server, the
  database and the login; SqlClient puts no password in it. A failure that is not SQL Server's,
  which is waited for on an Azure SQL name only, is named by its type and nothing else.
- `Applying migration` is EF's own line, once for each migration this run applied. `migrate` itself
  claims no count: two runs at once read the same pending list.
- A refusal or a failure ends in one Error whose line starts with the command's name: `migrate
  refused: …`, `seed failed: …`. A `failed:` line that reports an exception is followed by its
  stack. Errors can come before it: EF's own, and from `seed` and `reset` a `Failed to execute …`
  line that names the seeder. Whatever watches the log matches `failed:` and `refused:`.
- `seed` and `reset` also print one Warning that is not theirs and not a failure: ASP.NET Core's
  data protection, which Identity's token providers bring in, says where it would keep its keys
  ("Storing keys in a directory … that may not be persisted outside of the container").

## Things to know

- **Two `migrate` runs at once are safe.** On a migrated database both exit 0. The second waits
  for the first, which holds EF's migration lock; past the 30 s command timeout it exits 1 and can
  be run again. On a local server without the database, one creates it and the other exits 1.
- **A database ahead of the build is refused**, exit 1, and nothing is changed: an older build must
  not move an app onto a schema it does not know. Locally that is a volume a newer branch
  migrated: `docker compose down -v`.
- **SIGTERM cancels the run and the exit code is 1**, for each of the three commands. The last
  line says whether running it again is safe. A `migrate` that was stopped is: only migrations the
  history table does not list are applied. Measured in the image with SQL Server down, `docker
  stop` sent five seconds in: each command printed its line and exited 0.3 to 0.4 s after the stop
  was sent.
- **A command is cancelled only where its token reaches.** The signal cancels a token; the call
  that is in flight has to have been given it. Identity's managers take none, so the tool registers
  two that read it from `RunCancellation`, which the seeders' orchestrator sets. A new command
  that calls Identity another way sets it on its own scope first, or a stop does nothing until the
  process is killed (`seed` before this: exit 137 and no line).
- **`migrate` leaves a schema with no rows**: no roles, no users. The roles come from `seed`. Where
  `seed` never runs, whatever creates the first user has to create the roles.
- **`seed` fills an empty database only.** The seeders skip what is already there, so a seed that
  was cut short is not completed by running it again: it exits 1 every time, and `reset` starts
  over.
- **`seed` on a database people have used exits 0.** compose runs it on every `up`. Its final check
  counts the five demo accounts by their numbers, closed ones included: nothing in the app changes
  an account's number or removes its row, while a demo user can rename their handle. It also asks
  for at least the demo ledger's 26 rows: the app adds ledger rows and never removes one.
- **The Azure SQL rule goes by the server's name**: `.database.windows.net`,
  `.database.cloudapi.de`, `.database.usgovcloudapi.net`, `.database.chinacloudapi.cn` and
  `.database.fabric.microsoft.com`, the five SqlClient itself knows. An alias or an IP address in
  front of an Azure SQL server is not recognised.
- **On Azure SQL, give `migrate` a login that cannot create a database.** "Never creates one
  there" is the wait's rule: the database goes to EF only after one open of it succeeded, and the
  run stops when the server says the database is missing. A database that stopped answering after
  that open would still reach EF, which answers "cannot open" with `CREATE DATABASE`. That window
  was read in the code, not produced; the login's rights are what closes it. The deployment's
  identity is a user inside the one database, with no right on the server.
- **With a managed identity, "refused" and "no token" end differently.** A refused login means the
  database has no user for the identity: three Warnings, then `the login was refused three times`,
  seconds after the start. An identity that gets no token (it is not attached to the container,
  or the string names another client ID) is not an answer from SQL Server: on an Azure SQL name it
  is waited for, and the run ends when the wait is over. Neither has been produced on Azure. The
  first is what a wrong password does locally; the second was read in SqlClient 6.1.1.
- **On an Azure SQL name an answer the wait has no rule for is waited for too**, also one that
  waiting cannot change: the run then ends when the wait is over, with that answer in its last
  line. Off Azure such an answer goes to EF at once. What the wait has a rule for still ends the
  run sooner: a login refused three times in a row, and on an Azure SQL name a database the server
  does not hold.
- **A declined `reset` prompt exits 0.** Nothing was done and nothing failed. In a container there
  is no terminal: pass `--confirm`.
