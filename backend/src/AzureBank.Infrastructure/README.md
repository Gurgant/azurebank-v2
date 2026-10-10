# AzureBank.Infrastructure

The data layer: the EF Core context for SQL Server, the entity mappings, the migrations, the audit
trail's hash chain and the code that delivers owed notices. Every process that opens the database
gets its context here: the API, the notice-relay Function, the Seeder and the AuditVerifier. The
BFF has no database and does not reference this project. The entities themselves are in
[AzureBank.Shared](../AzureBank.Shared/README.md).

## What it holds

| Path | Holds |
| --- | --- |
| `Data/AzureBankDbContext.cs` | The context, and the rules every save passes through |
| `Data/Configurations/` | One mapping per entity: keys, widths, indexes, CHECK constraints |
| `Data/AuditChain.cs`, `Data/AuditAnchorChain.cs` | The hash chain over `AuditEvents` and the chain of anchor records (ADR-0044) |
| `Data/DesignTimeDbContextFactory.cs` | The context `dotnet ef` gets |
| `Extensions/ServiceCollectionExtensions.cs` | `AddInfrastructure`, and the connection limits (`SqlConnectionDefaults`) |
| `Migrations/` | The migrations and the model snapshot |
| `Notices/` | What every runner of the notice relay shares |

## The tables

| Table | A row is | Worth knowing |
| --- | --- | --- |
| `AspNetUsers`, with Identity's other tables | A user | `AzureTag` is unique, and so is `NormalizedEmail` where it is not null |
| `Accounts` | A bank account | Closed by soft delete: a query filter hides the rows with `IsDeleted`. One primary account per user and a unique number, both among the accounts not deleted. Carries a `rowversion` |
| `Transactions` | One ledger entry | Immutable through the change tracker (below). Amounts are `decimal(19,4)` |
| `RefreshTokens` | The grant of one BFF session, kept as a hash (ADR-0057) | Deleted with its user |
| `IdempotencyRecords` | One key of one user on one endpoint (ADR-0009) | The primary key is the lock |
| `StepUpAuthorizations` | One authorisation minted from a PIN, spent once (ADR-0042) | Nothing sweeps it by age |
| `AuditEvents` | One security event, chained to the one before (ADR-0044) | Never purged. `Sequence` is assigned by the chain, not by the database |
| `AuditAnchors` | What the chain looked like when the verifier ran | Insert-only |
| `SubscriberNotices` | A notice an account holder is owed (ADR-0045) | Holds no address. Deleted with its user |
| `DemoCopies` | One prepared copy of the public demo (ADR-0062) | No row outside the demo |

Every enum is stored as its member's name. Account, transaction and grant ids are UUID v7, made by
the process (`GuidVersion7ValueGenerator`) and not by the database.

## Use it

```csharp
services.AddInfrastructure(configuration, environment);
```

That binds the `Database` section and registers `AzureBankDbContext` on SQL Server, from
`ConnectionStrings:DefaultConnection`. It registers no other service. The
[local setup](../../../docs/engineering-practices.md#local-setup) sets that string for the API and
the Seeder.

- **The audit chain is the host's to register**: `services.AddScoped<IAuditChain, AuditChain>()`,
  as the API and the AuditVerifier do. A context without one saves everything except an audit row:
  a save that carries a new `AuditEvent` throws, because a row with no hash would read as audited
  and prove nothing.
- **Every `IInterceptor` the host registers is added to the context.** The API registers its commit
  gate that way (ADR-0058 D4); the other hosts register none.
- **`retryOnTransientFailures: false` is for a read-only consumer that streams a large result**,
  as the AuditVerifier does: with retries on, EF buffers every query's result before the first row
  is read. Leave it on for anything that writes.
- In Development the context logs parameter values and detailed errors, and nowhere else.

## What every save does

Every `SaveChanges` and `SaveChangesAsync` overload ends in the two this context overrides, so no
caller skips these steps:

1. **A tracked `Transaction` cannot be changed or deleted**: the save throws. One write is allowed,
   once: `RelatedTransactionId` from null to a value, which links the two rows of a transfer. A
   tracked `AuditAnchor` cannot be changed or deleted at all.
2. **Timestamps come from one clock read per save**: `CreatedAt` and `UpdatedAt` on an account and
   on a user, `CreatedAt` on a new transaction. Every other entity sets its own instants. The clock
   is the `TimeProvider` the constructor is given, or the system clock.
3. **Each new `AuditEvent` is chained to the row before it** (ADR-0044 D3). On SQL Server the save
   reads the tail under a lock and inserts in one transaction, its own or the caller's. When the
   commit of its own transaction fails, it asks the database whether its audit rows are there
   before anything is sent again (ADR-0058 D10).

**What these steps do not cover.** A set-based statement (`ExecuteUpdate`, `ExecuteDelete`, raw
SQL) tracks nothing and passes none of them: the Seeder deletes a demo copy's ledger that way, on
purpose (ADR-0062, decision 10). The steps guard against the application's own code, never against
whoever can write SQL to the database.

`Accounts` carries a global query filter on `IsDeleted`. A query that has to see closed accounts
calls `IgnoreQueryFilters()`.

## Connection limits and retries

Every host opens the database through the same defaults (ADR-0058 D1, D2):

| What | Default | Set by |
| --- | --- | --- |
| `Connect Timeout` | 10 s | `Database:ConnectTimeoutSeconds` |
| `ConnectRetryCount` | 0: EF is the only retry layer | `Database:ConnectRetryCount` |
| `Max Pool Size` | 12, and 5 in the Seeder | `Database:MaxPoolSize` |
| `Pool Blocking Period` | `NeverBlock` | the code |
| EF's retries | 4, with a back-off capped at 10 s | `Database:MaxRetryCount`, `Database:MaxRetryDelay` |
| Command timeout | 30 s | the code |

The first four are written into the connection string only where the string leaves that keyword
unset, under any of its names, so a value in a deployment's own string wins. A command that times
out (SqlClient's error -2) is not retried. Each retry is logged at Warning. The AuditVerifier runs
without retries. Only the API validates the `Database` section at startup.

## Migrations

A database is brought to the latest migration by the Seeder's `migrate`, locally and in a
deployment (ADR-0060), and a local one is rebuilt by its `reset`: see
[the Seeder's README](../../tools/AzureBank.Seeder/README.md). `dotnet ef` is what writes a
migration. Run it from `backend/`, where `.config/dotnet-tools.json` pins the tool:

```bash
dotnet tool restore

# Add a new migration
dotnet ef migrations add MigrationName \
  --project src/AzureBank.Infrastructure \
  --startup-project src/AzureBank.Api

# Generate SQL script
dotnet ef migrations script \
  --project src/AzureBank.Infrastructure \
  --startup-project src/AzureBank.Api \
  --output migration.sql

# Apply migrations to one database
dotnet ef database update --connection "<connection string>" \
  --project src/AzureBank.Infrastructure \
  --startup-project src/AzureBank.Api

# Take one database back to an earlier migration (0: to before the first)
dotnet ef database update PreviousMigrationName --connection "<connection string>" \
  --project src/AzureBank.Infrastructure \
  --startup-project src/AzureBank.Api
```

`dotnet ef` gets its context from `DesignTimeDbContextFactory`. It reads the API's
`appsettings.json` and `appsettings.Development.json`, both optional, and nothing else: no
environment variable and no user-secrets, so that a connection string left in a shell cannot aim
`database drop` at the database it names. The committed `appsettings.json` holds no connection
string, so a verb that opens a database takes `--connection`, unless the git-ignored
`appsettings.Development.json` holds one. `--connection` sets its string after the factory has
run: the run keeps the retry budget above and opens with that string's own limits (ADR-0060,
decision 8). `dotnet ef migrations remove`, which undoes the last `migrations add`, opens a
database too and has no `--connection`: with the committed settings it fails, and
[docs/engineering-traps.md](../../../docs/engineering-traps.md) has the way out.

## Notices

`Notices/` is what the three runners of the notice relay share, so that they cannot differ on what
"delivered" means: the API's loop (ADR-0048), the Function (ADR-0051) and the operator tool's
`notify` (ADR-0045). `NoticeClaim` leases a batch of owed rows in one statement, `NoticeSweep` is
one whole pass, `NoticeDeliveryRun` delivers one notice, `NoticeRenderer` writes its text and
`PickupDirectoryTransport` writes it as an `.eml` file into a directory.

- Nothing here sends mail: the last hop is a file in a pickup directory.
- Delivery is at-least-once (ADR-0048 D3).
- The address is read from the account at delivery and goes to the transport alone: it is in no
  row, result, log line or file name. It is inside each file, in the `To:` header, so the
  directory holds addresses at rest. `PickupDirectoryGuard` refuses a directory inside a git
  working tree.
- `Notices:Runner` names the one hosted runner, the API or the Function, and is `None` unless set;
  `notify` is not gated by it. The host it names refuses to start without a pickup directory and a
  contact (`NoticeRelayOptionsValidation`).

## Test it

From `backend/`:

```bash
dotnet test tests/AzureBank.Tests/AzureBank.Tests.csproj \
  --filter "FullyQualifiedName~AzureBank.Tests.Unit.Data"
```

Without a database the suite runs on EF's InMemory provider, which has no transactions, no
locks, no CHECK constraints, no filtered indexes and no foreign keys; there a `rowversion` is a
plain column, so a conflict on it never occurs. What depends on SQL Server is proved by the tests
marked `[SqlServerFact]`: they run when `AZUREBANK_TEST_SQLSERVER` holds a connection string, and
skip on an Azure SQL server name, because several of them create and drop databases.
More in [the tests' README](../../tests/AzureBank.Tests/README.md).

## Easy to get wrong

- **A change to an entity or to a mapping needs its migration.** Without one, `ModelSnapshotTests`
  fails, and so does the CI step that runs `dotnet ef migrations has-pending-model-changes`
  (ADR-0060, decision 9). A width in `ValidationRules` is a column width, so changing one is such
  a change.
- **`dotnet ef` reads the compiled assembly, not the source files**, and has other traps of its
  own: [docs/engineering-traps.md](../../../docs/engineering-traps.md).
- **Four index names are read by the API.** Its `ConcurrencyRetry` tells one unique-index failure
  from another by the index's name: `IX_Accounts_AccountNumber` and
  `IX_Transactions_TransactionNumber`, to generate the number again, and `IX_AspNetUsers_AzureTag`
  and Identity's `EmailIndex`, to answer the 409 of a handle or an e-mail already taken. Rename one
  and that recovery becomes a 500.
- **An enum's names are in the database.** Reordering members changes nothing; renaming one
  changes what the stored rows say. `RefreshTokens.RevokedReason` also has a CHECK constraint that
  lists its five names, so a sixth needs the constraint changed, and a migration.
- **A GUID id is not an order.** SQL Server sorts a `uniqueidentifier` by its last bytes first, so
  "newest" is read from a timestamp, and the audit chain's order from `Sequence`.

## See also

- [backend/README.md](../../README.md): the solution, and how to run it.
- [AzureBank.Shared](../AzureBank.Shared/README.md): the entities, the options and the limits.
- [The notice-relay Function](../AzureBank.Functions.NoticeRelay/README.md).
- [docs/adr](../../../docs/adr/README.md): the decision records cited here by number.
