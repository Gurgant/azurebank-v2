# AzureBank.Seeder

A console tool with five commands. `migrate` brings the database to the latest migration, `seed`
adds the demo data, `reset` drops the database and does both. `seed-pool` and `recycle` keep the
public demo's pool of private copies
([ADR-0062](../../../docs/adr/0062-demo-visitors-get-private-copies-from-a-prepared-pool.md)): the
first fills it, the second is the job that tops it up and deletes the copies whose time is over.
It is also what the tools image runs: the one-shot containers that prepare the database before the
API starts ([ADR-0060](../../../docs/adr/0060-migrations-run-as-a-one-shot-container-before-the-app.md)),
and that job.

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
`["migrate"]`, `["seed"]`, `["recycle"]`, `["seed-pool", "5"]`. With compose, arguments after the
service name replace the service's command, so an option needs the command again: `docker compose
run --rm migrate migrate --wait-seconds 120`. `compose.demo.yaml` at the root runs the demo's two
commands (its header says how).

**Configuration comes from the environment only.** An argument is never read as a setting, and the
parser prints back an argument it does not know, so no secret goes in the arguments. With no
`DOTNET_ENVIRONMENT` the tool runs as Production. The settings file beside the binary holds the
connection pool's size, the log levels and the demo data's public password and PIN; it holds no
connection string and nothing under `Demo`.

## Commands

| Command | Does | Needs | Runs |
|---|---|---|---|
| `migrate [--wait-seconds N]` | Waits up to N seconds (60; 0 to 600) for the database to accept a connection, then applies every pending migration; changes nothing when there is none. Off Azure it creates the database if the server has none by that name. On an Azure SQL server name it never creates one, and it goes on only once an open has succeeded | The connection string, with a login that may change the schema. **No pepper, no other secret** | Everywhere, a deployment included |
| `seed` | Adds the two roles, the four demo users with their public password and PIN, their five accounts and a 26-row ledger, to a database that has none of them. A database that was seeded is left as it is, exit 0, whatever its users did since | The connection string and the PIN pepper, which must be the API's | Local runs and CI. **Refuses an Azure SQL server name, demo mode, and a database that holds the demo pool's rows** |
| `reset [--confirm]` | Drops the database, migrates, seeds | The same as `seed` | Development and CI. **Refuses an Azure SQL server name, demo mode, and a database that holds the demo pool's rows** |
| `seed-pool [copies]` | Tops the demo pool up to N fresh free copies, 1 to 500; without N, to `Demo:Pool:TargetFree`. A copy is a demo user, two contacts, four accounts and a 26-row ledger, and none of its users has a password. Up to N, never by N: a pool that holds them already is left as it is. No daily ceiling: that is `recycle`'s. Deletes nothing | The connection string, the PIN pepper and its key id, which must be the API's, and `Demo__Enabled=true`. A login that reads and writes rows is enough | Where the demo runs, **an Azure SQL name included**. **Refused with the demo off** |
| `recycle` | Tops the pool up first, to `TargetFree` or to what the day's claims leave of `MaxClaimsPerDay`, whichever is lower; then deletes each claimed copy whose time is over and in which no session is live, and each free copy older than `MaxFreeAgeHours`; then removes the expired grants and idempotency records of anyone, as the API's own clean-ups do. Safe at any interval, and beside the API | The same as `seed-pool` | The same as `seed-pool`: the job a deployment schedules |

## Variables

| Variable | For | |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | every command | Required; the name the API reads. It has to name a server and a database. A password that holds `;`, `=` or a quote has to be quoted, or the string cannot be read |
| `Security__PinPepper` | `seed`, `reset`, `seed-pool`, `recycle` | 32 characters or more, equal to the API's (ADR-0011) |
| `Security__PinPepperKeyId`, `Security__PreviousPinPeppers__<id>` | the same four | Optional: 1 and none. Where the API sets them, the same values: a PIN hash carries the key id of the pepper that made it, and the API verifies it only with the pepper it holds under that id. The order of a rotation is in `docs/runbooks/demo-pool.md` |
| `Demo__Enabled` | `seed-pool` and `recycle`, which run only with `true`; `seed` and `reset`, which run only without it | Off by default |
| `Demo__CopyLifetimeHours`, `Demo__Pool__TargetFree`, `Demo__Pool__LowMark`, `Demo__Pool__MaxFreeAgeHours`, `Demo__Pool__MaxClaimsPerDay`, `Demo__Claim__MaxPerClientPerDay`, `Demo__Copy__MaxWrites` | `seed-pool`, `recycle`; `seed` and `reset` refuse a value out of range or unreadable too | Optional: 24, 50, 20, 44, 150, 10 and 200. The ranges are in ADR-0062, decision 3; the last two belong to the claim and are only checked here. `recycle` deletes a claimed copy by `Demo__CopyLifetimeHours`: where the API reads it too, give the job the API's value, or a copy can be deleted before the API ends it |
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
| 0 | Done. Running it again is safe and changes nothing; for `seed-pool` and `recycle`, it does what has come due since, such as building again the free copies that have grown too old to count |
| 1 | Failed after the server was contacted, or cancelled, or the command line was wrong. Running it again is safe. It helps when the cause passes: the database was not reachable in time, another run held the lock, the run was stopped. It does not help until something else changes for: a refused login; a database this login cannot open; a database that is ahead of the build; a missing database on Azure SQL; a model change with no migration; an incomplete seed (run `reset`) |
| 2 | Refused, and nothing was written. Before any connection was opened: a required variable is missing, too short or out of range, a variable holds a value that cannot be read as its type (named by its key, never by the value), the connection string names no database, `migrate` was given a connect timeout of 0, `seed` or `reset` was aimed at an Azure SQL server name or run in demo mode, or `seed-pool` or `recycle` was run with the demo off. After one count: `seed`, or `reset` after its prompt and before its drop, on a database that holds the demo pool's rows |
| 10 to 15 | `seed-pool` and `recycle` only: the run finished, and the code names the count to read on its line. 10 the pool was low, 11 it was empty, 15 the day's claims held the top-up back: done, with a signal. 12 a copy could not be built and the pool ended short, 13 a user outside every copy exists, 14 a copy could not be deleted: these need a look. `seed-pool` exits 12 or 13 only, and 13 only when it wrote nothing: users, and not one pool row. What each means, and the SQL behind it: `docs/runbooks/demo-pool.md` |

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
- `seed`, `reset`, `seed-pool` and `recycle`, once a run gets as far as Identity, also print one
  Warning that is not theirs and not a failure: ASP.NET Core's data protection, which Identity's
  token providers bring in, says where it would keep its keys ("Storing keys in a directory … that
  may not be persisted outside of the container"). A refusal, and a run that fails before it
  reaches Identity, print none.
- `seed-pool` and `recycle` end with one line that carries every count, at Information when they
  exit 0 and at Warning when they end with a signal:

  ```text
  [01:31:38 WRN] pool: free=6 was=5 claimed=0 claims24h=0 clientsAtCap=0 seeded=1 deleted(expired=0 hardStop=0 staleFree=0 failed=0) swept(idempotency=0 grants=0) tombstones=0 foreignUsers=0 ceiling=no result=PoolLow
  ```

  A run that did not finish prints no such line: it ends with a `failed:` line, or says it was
  cancelled. A copy that could not be built or deleted is an Error of its own, `Demo copy <id> could
  not be built (error <number>)` or `… deleted …`, with SQL Server's error number, or `null` when
  the failure was not SQL Server's (Identity refusing a user, say), and the run goes on: its code
  says whether that left the pool short (12) or a copy behind (14). The run's own lines name a copy
  by its id, never by an address. A value SQL Server refused can be printed all the same: its
  message for a duplicate quotes the value, and EF logs that message at Error, as does the run's
  line that follows it. Such a value is a handle, an account number or a transaction number another
  copy holds, never an address: Identity refuses an address that is taken, by its code, before
  SQL Server sees it.

## Things to know

- **Two `migrate` runs at once are safe.** On a migrated database both exit 0. The second waits
  for the first, which holds EF's migration lock; past the 30 s command timeout it exits 1 and can
  be run again. On a local server without the database, one creates it and the other exits 1.
- **A database ahead of the build is refused**, exit 1, and nothing is changed: an older build must
  not move an app onto a schema it does not know. Locally that is a volume a newer branch
  migrated: `docker compose down -v`.
- **SIGTERM cancels the run and the exit code is 1**, for each of the five commands. The last
  line says whether running it again is safe. A `migrate` that was stopped is: only migrations the
  history table does not list are applied. A pool run is too: a copy is built or deleted whole or
  not at all. Measured in the image with SQL Server down, `docker stop` sent five seconds in:
  `migrate`, `seed` and `reset` each printed its line and exited 0.3 to 0.4 s after the stop was
  sent. Measured with the server up, `seed-pool` stopped while it was building copies: its line,
  exit 1, 0.7 s, and no copy without its three users.
- **A command is cancelled only where its token reaches.** The signal cancels a token; the call
  that is in flight has to have been given it. Identity's managers take none, so the tool registers
  two that read it from `RunCancellation`, which the seeders' orchestrator sets, and the demo
  pool's builder for `seed-pool` and `recycle`. A new command that calls Identity another way sets
  it on its own scope first, or a stop does nothing until the process is killed (`seed` before
  this: exit 137 and no line).
- **`migrate` leaves a schema with no rows**: no roles, no users. The roles come from `seed`. Where
  `seed` never runs, whatever creates the first user has to create the roles.
- **`seed` fills an empty database only.** The seeders skip what is already there, so a seed that
  was cut short is not completed by running it again: it exits 1 every time, and `reset` starts
  over.
- **`seed` on a database people have used exits 0.** compose runs it on every `up`. Its final check
  counts the five demo accounts by their numbers, closed ones included: nothing in the app changes
  an account's number or removes its row, while a demo user can rename their handle. It also asks
  for at least the demo ledger's 26 rows: the app adds ledger rows and never removes one. `recycle`
  deletes a demo copy's, and only on a database that holds the demo pool, which `seed` refuses.
- **`seed` refuses the public demo's database**, whose demo is the pool's private copies. With
  `Demo__Enabled=true` it opens nothing. With the flag off it counts the pool's rows first, and one,
  the record of a deleted copy included, ends it with exit 2 before it writes anything: with
  `reset`'s, below, the one exit 2 that comes after a connection was opened.
- **`seed-pool` and `recycle` run on an Azure SQL name**, where `seed` and `reset` are refused: the
  demo's database is one. Their writes are copies whose users have no password and deletes keyed on
  a copy, so a user outside every copy matches none of them, apart from `recycle`'s two sweeps of
  expired grants and idempotency records, which take anyone's, as the API's own clean-ups do;
  neither drops, creates or migrates a database; and on a database with users and not one pool row
  both write nothing and exit 13.
- **Give them a login that reads and writes rows and nothing more**: where a deployment gives the
  app and its migration database users of their own, the app's, never the migration's. Measured
  2026-10-03 on the compose SQL Server and on LocalDB, a login with `db_datareader` and
  `db_datawriter` alone ran `seed-pool` and `recycle` through a whole cycle (roles, copies, a
  claimed copy and a stale one deleted, the sweeps), exit 0, and was refused `CREATE TABLE` (262).
  On the compose server, the same login without `db_datawriter` made `seed-pool` exit 12, every
  copy refused with 229 on `DemoCopies`.
- **`seed-pool` exits 0 on a pool it found low or empty**: filling it is what it was run for. A
  one-shot that exits with anything else stops the stack that waits for it, so only 12 and 13 come
  from it, and 13 only when it wrote nothing. A user outside every copy beside a pool is counted on
  its line (`foreignUsers`), and `recycle` exits 13 for it.
- **Two pool runs at once can build more than the target.** Each tops up from its own count: a
  `seed-pool` started while a scheduled `recycle` builds, or two `recycle` runs that overlap, can
  each build the copies the pool lacked, up to twice the target and up to twice what the day's
  claims leave room for. Every copy is still whole, nothing is deleted twice, and the extra copies
  are deleted when they grow too old to count. Measured on LocalDB with a run held in its top-up
  while another ran whole: a target of 2, both exited 0, 4 free copies. Give the job a schedule
  whose runs end before the next starts, and refill by hand between two runs.
- **`reset` refuses the demo's database too**, with the flag off, as `seed` does: after its prompt
  and before its drop it reads whether the pool's migration was applied and counts the pool's rows,
  and one row, a record included, ends it with exit 2. A database that does not exist, or one
  migrated before the pool's table, is reset as before.
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
  or the string names another client ID) is waited for, and the run ends when the wait is over:
  SqlClient reports it as an error numbered 0 of class 20, which the wait takes for a server that
  did not answer. `migrate` has produced neither on Azure. A throwaway program on SqlClient 6.1.1
  got both answers there on 2026-10-02: 18456, class 14, for the missing user, and number 0,
  class 20, around `Azure.Identity.AuthenticationFailedException`, for the missing token
  ([infra/README.md](../../../infra/README.md), "Measured on Azure").
- **On an Azure SQL name an answer the wait has no rule for is waited for too**, also one that
  waiting cannot change: the run then ends when the wait is over, with that answer in its last
  line. Off Azure such an answer goes to EF at once. What the wait has a rule for still ends the
  run sooner: a login refused three times in a row, and on an Azure SQL name a database the server
  does not hold.
- **A declined `reset` prompt exits 0.** Nothing was done and nothing failed. In a container there
  is no terminal: pass `--confirm`.
