# ADR-0060: Migrations run as a one-shot container before the app

**Status:** Accepted · **Date:** 2026-10-01 · **Decision Makers:** Vladislav Aleshaev ·
**Amends** [ADR-0058](0058-the-api-gives-up-cleanly-when-the-database-is-down.md) (its second
precondition is met, and its first says where `migrate` fits in the count of connections) and
[ADR-0011](0011-pin-hash-pepper.md) (where the Seeder checks the pepper)

**Where the code's citations point.** The code cites this record as "ADR-0060", with no section:
`MigrateCommand.cs` and `DatabaseGate.cs` for decisions 2 to 6, `DesignTimeDbContextFactory.cs` for
decision 8, and `compose.yaml` for decision 10. The commands' variables and exit codes are in
`backend/tools/AzureBank.Seeder/README.md`, which is the text that changes when a command is added.

## Context

Measured on 2026-10-01, before this decision, on LocalDB and on the compose SQL Server
(`2022-CU27`), with the Seeder as it stood.

**Nothing migrated the database without dropping it, except `dotnet ef` by hand.**

- `reset --confirm` drops the database, migrates and seeds. It is what creates the schema in three
  CI jobs and what `compose.yaml`'s header told a reader to run from the host.
- `seed` runs the four seeders and migrates nothing.
- `dotnet ef database update` goes through `DesignTimeDbContextFactory`, which required
  `../AzureBank.Api/appsettings.json` relative to the current directory and applied neither the
  connection limits nor a retry budget (ADR-0058's second precondition).

**The Seeder could not be a job.**

| What was run | What happened |
|---|---|
| `seed`, `--help` or no command, with no PIN pepper set | An unhandled `OptionsValidationException` before the command line was read: exit 139 in a Linux container, -532462766 as Windows reports it. The pepper was validated in `Program.cs` for every command |
| `reset --confirm` started from `backend/`, as CI starts it | 874 lines of output, 169 of them "Executed DbCommand", and a pool of 12. From its own folder: 17 lines, none of them a command, and the pool of 5 its settings ask for. The content root was the current directory |
| any command with no connection string (read in its settings, not run) | Its `appsettings.json` shipped `Server=localhost` as the default string: a container with no secret mapped would have gone looking for a server there instead of saying so |
| `seed` with a password the identity rules refuse (read in the seeders; reproduced on this change with its final check taken out) | Exit 0 with no user, no account and no ledger: a user that fails to be created is logged and skipped, and each seeder after it skips quietly when the one before left nothing |
| `docker stop` during `migrate`'s wait (on this change, before decision 7 was added to it) | Exit 143 in under a second and not a line from the command |

**EF's migrator does not survive the outage a one-shot has to survive.**

| What was run | What happened |
|---|---|
| SQL Server stopped for the whole run | Exit 1 after 75.7 s, four retry Warnings: the budget, as designed |
| SQL Server stopped, the run started, the server started 15 s later | **Exit 1.** One retry Warning, then an unretried `SqlException`, "TCP Provider, error: 35", number 0, class 20. The migrator opens its connection and takes its lock outside the execution strategy |
| One open every 0.5 s across three stop/start cycles | 142 succeeded; 7 answered 4060 (class 11) for a database the server held and had not brought online; 5 were 11001, 3 number 0 and 1 number 258, all class 20 |
| A wrong password | Exit 1 after 61.1 s and one line of output: EF retries a refused login every 500 ms for a minute and logs nothing |
| One row added to `__EFMigrationsHistory` | **Exit 0**, "already up to date", with 17 migrations applied and 16 known |

A 4060 matters beyond the wait: EF reads it as "the database does not exist" and answers with
`CREATE DATABASE`. On a local server that fails on a name that is taken. On Azure SQL it would
create a second database at the service's default size.

## Decision

**1. The schema is migrated by a one-shot that ends before the app starts, never by the API as it
starts.** Locally compose runs it; a deployment runs it as a job and moves the app only after it
exits 0.

**2. The one-shot is the Seeder's `migrate` command, through `AddInfrastructure`.** It opens with
the connection limits and the retry budget every host has (ADR-0058) and logs them before it opens
anything. It needs the connection string and no other secret: it never reads the PIN pepper. One
tools image holds the Seeder and nothing else; its entrypoint is `dotnet azurebank-seeder.dll`, so
a container's arguments are the command.

**3. `migrate` waits for the database before it hands it to EF**: up to 60 s (`--wait-seconds`, 0
to 600), one open every 2 s, a Warning for every attempt that failed.

| The open | Then |
|---|---|
| succeeds | go on |
| the server did not answer (class 20 or above, or number -2) | wait |
| the login was refused (18456) | wait; the third time in a row ends the run |
| the database cannot be opened (4060) | ask `master`: a database that is there and not online is waited for; one that is online and still cannot be opened ends the run the third time in a row; one that is not there is EF's to create, except on an Azure SQL name |
| anything else | go on: EF's own strategy and message apply |

**4. Three exit codes.** 0: done, and a rerun changes nothing. 1: failed after the server was
contacted, or the command line was wrong. 2: refused before any connection was opened. A missing
pepper or connection string is a 2 with a sentence. A connection string SqlClient cannot parse is
a 2 with a fixed sentence, never the parser's message, which quotes the string.

**5. On an Azure SQL server name only `migrate` runs, and it never creates a database there.**
`seed` and `reset` refuse the name with exit 2: `seed`'s four users have a password and a PIN that
are in this repository, and `reset` drops the database. A missing database on Azure SQL ends
`migrate` with exit 1; the infrastructure creates databases. The rule goes by the server's name,
on the five suffixes SqlClient itself treats as Azure SQL.

**6. A database that holds a migration the build does not know is refused**, exit 1, checked before
and after `MigrateAsync`. A newer build migrated it, and an older build must not move an app onto
a schema it does not know. The command claims no "applied n": two runs at once read the same
pending list before EF's lock. EF's own `Applying migration '…'.` lines are the record.

**7. SIGTERM cancels the command.** `Program.cs` registers for it. The command says what was cut
short and whether a rerun is safe, and exits 1.

**8. The design-time factory gets the limits and the budget, and reads no environment variable.**
The API's settings are optional. `dotnet ef … --connection` still sets its string after the
factory has run, so such a run keeps the retry budget and opens with its own string's limits.

**9. CI checks the model against the migrations**
(`dotnet ef migrations has-pending-model-changes`, its success line asserted), in the job that
builds, before any job creates a database.

**10. compose runs `sqlserver` (healthy) → `migrate` → `seed` → `api`.** `seed` is for local runs
and CI.

## Rejected

- **An EF migrations bundle in the image.** It fails the outage above for the same reason
  `dotnet ef` does, and the fix is code that runs before the migrator: in the Seeder that is a class
  with tests, around a bundle it is a shell script in the image. `--connection` reaches a bundle
  after the factory has run, so it would open without the limits. A bundle built from this
  repository (23.6 s, 56.7 MB) and run from another folder failed with "Unable to create a
  'DbContext'" until the factory stopped requiring the API's folder. And the Seeder is in the image
  anyway.
- **Migrating as the API starts.** Two replicas during a revision overlap would both migrate, the
  app's login would need the right to change the schema, and a failed migration would be a host
  that does not start instead of a job that says why.
- **Adding 0 and 258 to EF's retry list for every host.** It would not reach the open that fails:
  that open is outside the strategy. And a request would retry what its 40 s deadline exists to
  bound.
- **A Warning for a database ahead of the build.** The deployment moves the app next. A rollback is
  a migration that goes back, written on purpose.
- **Reading the engine edition after connecting, to catch an alias or an address in front of Azure
  SQL.** Nothing in this repository reaches Azure SQL under another name, the branch that matters
  cannot be produced on LocalDB or the container, and exit 2 would stop meaning "nothing was
  opened". It can be added against a real server.
- **An environment variable as a source for the design-time factory.** Nothing uses it, and it
  would aim `dotnet ef database drop` at whatever a shell still holds.
- **Hiding an argument the parser does not know.** System.CommandLine prints it back. No secret
  goes in the arguments; the README says so.

## Consequences

**Positive**

- `docker compose up --build -d` is the whole recipe.
- A deployment has a migration step with an exit code it can act on and a log that says what it
  waited for.
- ADR-0058's second precondition is met for the deployment's road.
- A model change with no migration fails the first CI job, not the fourth.

**Negative**

- Locally, `docker compose up` on a volume a newer branch migrated stops at `migrate` with exit 1,
  until `docker compose down -v`. A deployment of an older commit stops there too, before the app
  moves.
- `seed` exits 1 where it exited 0 with nothing or half seeded, and a seed that was cut short is
  not completed by running it again: `reset` starts over.
- The Azure SQL rule does not see an alias or an IP address.
- `--connection` on `dotnet ef` still opens with SqlClient's own limits.
- The `sqlserver` job in CI creates three more databases.

**Neutral**

- How CI creates its databases is unchanged: `reset --confirm`, then `seed`. Its seeding log is
  shorter, because the Seeder now reads its own settings from any folder.
- The API and BFF images are unchanged apart from the source label.
- `migrate` leaves a schema with no rows. The roles come from `seed`; where `seed` never runs,
  whatever creates the first user creates the roles.

## Validation

**Tests** (`backend/tests/AzureBank.Tests`). Each guard below was removed again, one at a time, and
the test that covers it failed: 19 removals, 19 failures.

- `Unit/Tools/DatabaseGateTests`: the wait as a script of server answers on a fake clock.
- `Unit/Tools/ConnectionTargetTests`: the Azure SQL name rule, and what counts as missing or
  unreadable.
- `Unit/Tools/SeederCommandTests`: every refusal on the tool's own composition root, with an
  interceptor that counts the opens EF starts, and one test in which it counts one.
- `Unit/Data/DesignTimeFactoryTests`: the factory's limits, its budget, and
  `HasPendingModelChanges()`.
- `Integration/MigrateCommandSqlServerTests`, `SeedCommandSqlServerTests`: each command on a
  database of its own, three acts each.
- `Integration/SeederProcessTests`: the real `azurebank-seeder.dll` as a child process.

**Measured on 2026-10-01** on the compose stack under a private project name, the three images
built from this change. One run per row unless it says otherwise.

| What was run | What happened |
|---|---|
| `up -d` from an empty volume | `migrate` exited 0: the limits line with pool 5, "Pending migrations: 16", 16 "Applying migration" lines, "16 of 16 migrations", in 6.1 s the first time and 2.5 s on a later run. `seed` exited 0. `/health/ready` answered 200. 26 transactions (22 on John's accounts), 4 users, 5 accounts, 2 roles, 16 migrations |
| `run --rm migrate`, twice | Exit 0 both, "Pending migrations: 0", 4.0 s and 3.5 s by its own clock |
| `up -d` again, then `up -d --wait` | Exit 0 both. The one-shots ran again and changed nothing; the API was not restarted |
| `run --rm seed` again | Exit 0, the same counts |
| SQL Server stopped, `migrate` started, the server started 15 s later (two runs) | **Exit 0** both. 5 Warnings and 39.7 s; 2 Warnings and 20.8 s. No Error line. Among the first run's Warnings, two 4060: the answer EF would have met with `CREATE DATABASE` |
| the same with `--wait-seconds 0` | Exit 1 after one attempt, before the server was back |
| SQL Server stopped for the whole run | Exit 1 after 70.5 s, "did not accept a connection within 60 s" with the last answer; a rerun with the server up: exit 0 |
| SQL Server paused, unpaused 15 s later | Exit 0, one Warning, number -2 |
| SQL Server paused, `--wait-seconds 20` | Exit 1 after 24.5 s |
| a wrong password | Exit 1, three Warnings with 18456 four seconds apart in all, "the login was refused three times" |
| one row added to the history table | Exit 1 naming the row, the table unchanged; with the row removed, exit 0 |
| EF's lock held 20 s by another session | Exit 0 after the lock was released, 17.3 s |
| EF's lock held 45 s by another session (LocalDB) | Exit 1 after 31.8 s, "Execution Timeout Expired": the 30 s command timeout. A rerun exited 0 |
| two `migrate` runs started together (LocalDB) | On a server without the database: one exited 0 with 16 of 16, the other 1. On the migrated database: both 0 |
| `seed` and `reset --confirm` on an Azure SQL name, no pepper, no network | Exit 2 both, each with its refusal, in about a second |
| the same two on a name that is not Azure's and does not resolve | Exit 1 both: they tried to open |
| `docker stop` during the wait | Exit 1 and the cancellation line, in under a second. Before decision 7: exit 143 and no line |
| `docker stop` while waiting for EF's lock, then a rerun | Exit 1 and the cancellation line; the rerun exited 0 with 16 of 16 |
| no arguments and no variables; `migrate` with no variables | Exit 1 and the usage; exit 2 naming the variable |
| canary `appsettings.Development.json` and `.env.canary` in the Seeder's and the API's folders | In none of the three images. A control build without the three exclusion rules held the settings file |
| `docker image inspect` of the three | User 1654 and the source label on each |

The CI step, on a Release build of the solution: exit 0 and its success line in 3.3 s; with an
`int` property added to `Account`, exit 1.

**Not measured:** anything on Azure SQL (the name rule's refusals were measured with a name nobody
can register, and the "missing database" verdict with a scripted answer); the CI step on the
runner; the time the three new databases add to the SQL job.

## What would change this

- A migration whose statement runs longer than the 30 s command timeout: `migrate` gets a timeout
  of its own.
- A rollback that must pass over a newer schema: a flag, with its own record.
- A server reached under a name that is not Azure's: the engine-edition check, measured there.
- A wait that 60 s does not cover where it is deployed: the default moves, with the measurement.

## Related

- [ADR-0058](0058-the-api-gives-up-cleanly-when-the-database-is-down.md): the limits and the retry
  budget this command opens with, and the two preconditions this record amends.
- [ADR-0011](0011-pin-hash-pepper.md): the pepper `seed` and `reset` check.
- `backend/tools/AzureBank.Seeder/README.md`: the commands, their variables and exit codes.
