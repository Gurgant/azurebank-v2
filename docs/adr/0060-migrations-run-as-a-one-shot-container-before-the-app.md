# ADR-0060: Migrations run as a one-shot container before the app

**Status:** Accepted · **Date:** 2026-10-01 · **Amended:** 2026-10-03 (ADR-0062), 2026-10-05
(ADR-0064) · **Amends:** ADR-0011, ADR-0058 · **Decision Makers:** Vladislav Aleshaev

## Context

A deployment needs a step that brings the schema up to date without dropping the database, and
that survives a database that is not up yet. EF's migrator does not: it opens its connection and
takes its lock outside the execution strategy, so a server that comes up late fails the run. It
reads a 4060 as "the database does not exist" and answers with `CREATE DATABASE`, which on Azure
SQL would create a second database at the service's default size. And it reports a database that
holds a migration the build does not know as up to date. The commands' variables and exit codes
are in [the Seeder's README](../../backend/tools/AzureBank.Seeder/README.md).

## Decision

1. **The schema is migrated by a one-shot that ends before the app starts, never by the API as it
   starts**: compose runs it locally, and a deployment runs it as a job and moves the app only
   after it exits 0, because a failed migration is then a job that says why.
2. **The one-shot is the Seeder's `migrate` command, through `AddInfrastructure`**, so that it opens
   with the connection limits and the retry budget every host has (ADR-0058, whose second
   precondition this meets), in a pool of 5. It needs the connection string and no other secret,
   never the PIN pepper, and ships in one tools image that holds the Seeder alone.
3. **`migrate` waits for the database before it hands it to EF**: up to 60 s (`--wait-seconds`, 0
   to 600), one open every 2 s and a Warning for each that failed, because EF's migrator does not
   retry its first open and answers a 4060 with `CREATE DATABASE`.

   | The open | Then |
   |---|---|
   | succeeds | go on |
   | the server did not answer (class 20 or above, or number -2) | wait |
   | the login was refused (18456) | wait; the third time in a row ends the run |
   | the database cannot be opened (4060) | ask `master`: there and not online, wait; online and still not opened, end the run the third time in a row; not there, EF creates it, except on an Azure SQL name |
   | anything else, SQL Server's or not | off Azure, go on to EF; on an Azure SQL name, wait: Azure SQL answers 40613 and 40197 (class 17) and 49918 (class 16) while a database is not available (Microsoft's table of transient faults), so there only an open that succeeded hands the database to EF |

4. **Three exit codes**: 0, done, and a rerun changes nothing; 1, failed after the server was
   contacted, or the command line was wrong; 2, refused, and nothing was written. All 2s but two
   come before any connection is opened: a missing pepper or connection string; a string SqlClient
   cannot parse (a fixed sentence, because the parser's message quotes the string) or that names no
   database (`migrate` would build the schema in the login's default one); for `migrate`, a connect
   timeout of 0, which SqlClient reads as no limit; a demo setting out of range or unreadable as its
   type; `seed` and `reset` in demo mode; `seed-pool` and `recycle` with the demo off. Two 2s come
   after a connection was opened: `seed` and `reset` refuse a database that holds the demo pool's
   rows. The pool's commands also end with a code from 10 to 15 (ADR-0062, decision 12).
5. **On an Azure SQL server name `seed` and `reset` are refused (exit 2), and `migrate` never
   creates a database there**: `seed`'s four users have a password and a PIN that are in the
   repository, `reset` drops the database, and a missing database ends `migrate` with exit 1,
   because the infrastructure creates databases. The rule goes by the server's name, on the five
   suffixes SqlClient itself treats as Azure SQL.
   Note: `seed-pool` and `recycle` run on an Azure SQL name too, because the public demo's
   database is one (ADR-0062). That is safe where `seed` and `reset` are not: the users they write
   have no password, their deletes are keyed on a copy, on a database with users and no pool row
   they write nothing, and neither drops, creates or migrates a database. They run as the app's
   database user, never the migration's (on Azure `azurebank_app`, not `azurebank_migrator`),
   because every statement reads or writes rows, and the migration's user would give a scheduled
   job the right to change the schema.
6. **A database that holds a migration the build does not know is refused**, exit 1, checked
   before and after `MigrateAsync`, because an older build must not move an app onto a schema it
   does not know. EF's own `Applying migration` lines are the record of what was applied.
7. **SIGTERM cancels the command**: the token goes to every call to the database, Identity's
   managers included, and the command says what was cut short, whether a rerun is safe, and exits 1.
8. **The design-time factory gets the limits and the budget, and reads no environment variable.**
   The API's settings are optional to it. `dotnet ef … --connection` sets its string after the
   factory has run, so such a run keeps the retry budget and opens with its own string's limits.
9. **CI checks the model against the migrations**, in the job that builds, which opens no database
   (`dotnet ef migrations has-pending-model-changes`), so that a model change with no migration
   fails there and not in the jobs that create a database.
10. **compose runs `sqlserver` (healthy) → `migrate` → `seed` → `api`.** Both one-shots run again on
    every `up`, so `seed` exits 0 on a database people have used, and 1 on a half-seeded one: its
    final check asks for at least the ledger's 26 rows and for the five demo accounts by number,
    closed ones included, because the app never changes an account's number or removes its row.

## Rejected

- Rejected: an EF migrations bundle, because it fails the same outage and its fix is a shell script.
- Rejected: migrating as the API starts, because two replicas would both migrate, and the app's
  login would need the right to change the schema.
- Rejected: adding 0 and 258 to EF's retry list, because the failing open is outside the strategy.
- Rejected: a Warning for a database ahead of the build, because the deployment moves the app next.
- Rejected: reading the engine edition once connected, because nothing in the repository reaches
  Azure SQL under another name, and that branch cannot be produced on a local SQL Server.
- Rejected: an environment variable as a source for the design-time factory, because it would aim
  `dotnet ef database drop` at whatever a shell still holds.
- Rejected: hiding an argument the parser does not know, because no secret goes in the arguments.

## Consequences

- A database a newer build migrated stops `migrate`, and with it an older commit's deployment
  before the app moves; locally, until `docker compose down -v`.
- `migrate` leaves a schema with no rows: the roles come from `seed`, or from the demo pool's
  first run (ADR-0062), and a registration before either is answered 500.
- The SQL Server tests skip on an Azure SQL name, because several create and drop databases.
- Not covered: the Azure SQL rule does not see an alias or an IP address.
- Not covered: a database that stops answering after the wait's open still reaches EF, which
  answers with `CREATE DATABASE`: the login `migrate` is given must not be able to create one.
- Not covered: on an Azure SQL name an answer that waiting cannot change takes the whole wait.
- Not covered: a seed that was cut short is not completed by a second run: `reset` starts over.

## Revisit when

- A migration's statement runs longer than the 30 s command timeout: a timeout of its own.
- A rollback must pass over a newer schema: a flag, with its own record.
- A server is reached under a name that is not Azure's: the engine-edition check, measured there.
- 60 s does not cover the wait where it is deployed: the default moves, with the measurement.

## Verified by

- `DatabaseGateTests`, `ConnectionTargetTests`, `SeederCommandTests`, `DesignTimeFactoryTests`.
- On SQL Server: `MigrateCommandSqlServerTests`, `SeedCommandSqlServerTests`, `SeederProcessTests`.

## Related

ADR-0011, ADR-0058, ADR-0061, ADR-0062, ADR-0064.
