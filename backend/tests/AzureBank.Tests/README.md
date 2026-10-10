# AzureBank.Tests

The xUnit tests of the API, of the libraries under it, of the notice relay function and of the two
tools, the Seeder and the audit verifier. The BFF's tests are a project of their own,
`../AzureBank.Bff.Tests`. Parent: [the backend README](../../README.md).

## What is here

| Folder | Holds |
|---|---|
| `Unit/` | One class at a time: validators, services, options, middleware, the tools' commands |
| `Integration/` | The API hosted inside the test process by `CustomWebApplicationFactory`, on EF Core's in-memory provider. Nearly all the tests that run on a real SQL Server instead are here too (below) |
| `Architecture/` | Rules read from the assemblies (layer dependencies, naming, design) and from files of the repository: source files, the committed contract `docs/api/openapiv1.json`, the Bruno collection, the audit pages under `docs/` |
| `Fixtures/` | The factory, the SQL Server gate, EF interceptors that inject one fault, `BffOverApiFactory` (the BFF hosted in front of that API, in the same process), and other helpers |

Written with xUnit, Moq, FluentAssertions, `Microsoft.AspNetCore.Mvc.Testing`, EF Core's in-memory
provider, `FakeTimeProvider` and NetArchTest (eNhancedEdition). The versions are in
`backend/Directory.Packages.props`.

## Running Tests

From this folder. CI runs the same project from `backend/`, by its path:
`dotnet test tests/AzureBank.Tests/AzureBank.Tests.csproj`. The SQL Server tests skip unless
the variable of the next section is set.

```bash
dotnet test                                                   # every test
dotnet test --logger "console;verbosity=detailed"             # with detailed output
dotnet test --logger "trx;LogFileName=results.trx"            # with a TRX report
dotnet test --filter "FullyQualifiedName~AuthServiceTests"    # one class
dotnet test --filter "Name~Login"                             # the tests whose name holds a word
dotnet test --filter "Category=SqlServer"                     # the SQL Server ones alone
dotnet test --filter "Category!=SqlServer"                    # everything but them
```

Code coverage, and an HTML report of it (the second line installs the report tool once):

```bash
dotnet test --collect:"XPlat Code Coverage"
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:"**/coverage.cobertura.xml" -targetdir:"coveragereport" -reporttypes:Html
```

Before a run on Windows, stop the API and the BFF: a running one locks the build
([Local setup](../../../docs/engineering-practices.md#local-setup), "Running the tests").

## The tests that need SQL Server

A test that depends on what only a real database does (locks, unique indexes under a race,
concurrency tokens in a transaction) is marked `[SqlServerFact]` or `[SqlServerTheory]`. It runs
when `AZUREBANK_TEST_SQLSERVER` holds a connection string and is skipped without one. No test
starts a container: the server is the one the variable names, LocalDB on a development machine, a
service container in CI.

```bash
AZUREBANK_TEST_SQLSERVER="Server=(localdb)\\MSSQLLocalDB;Database=AzureBankProofs;Trusted_Connection=True;TrustServerCertificate=True" \
  dotnet test --filter "Category=SqlServer"
```

- **Read the counts, not the exit code.** Without the variable every one of these tests is
  skipped and `dotnet test` still exits 0: the filtered run above then passed nothing. CI's job
  for them fails when the number executed is 0 or differs from the number passed.
- **They skip on an Azure SQL server name too.** Several create and drop databases on the server
  the variable names, and on Azure SQL each one would be a paid database
  (`SqlServerFactAttribute.SkipReason`).
- **Most of them share the one database the variable names.** Give a test's users an address
  and a handle no other test uses.
- **A new one carries three attributes**: the gate on the method, and on the class the trait the
  filter selects and the collection that runs these classes one after another. Each host migrates
  the database as it starts, and two that migrate a new database at once fail. A plain `[Theory]`
  in place of `[SqlServerTheory]` fails on the connection where it should skip.

  ```csharp
  [Trait("Category", "SqlServer")]
  [Collection(SqlServerProofsCollection.Name)]
  public class SomethingSqlServerTests
  {
      [SqlServerFact]
      public async Task TheClaimThisPins()
      {
          using var factory = new CustomWebApplicationFactory();
          factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
          _ = factory.CreateClient(); // builds the host, which runs the migration
          // …
      }
  }
  ```

## Easy to get wrong

- **`Category=SqlServer` is the only category.** No other test carries a trait, so
  `--filter "Category=Unit"`, `"Category=Integration"` and `"Category=Architecture"` match no test
  and still exit 0. A run that executed nothing is not a run that passed.
- **To run both test projects, name the solution**: `dotnet test AzureBank.slnx` from `backend/`.
  A filter on the project's name leaves the BFF's tests out and reports success
  ([engineering traps](../../../docs/engineering-traps.md#frontend-test-infrastructure)).
- **The test host is not the production host.** The factory replaces the `DbContext`
  registration: in memory by default, one database for each factory instance, and on SQL Server
  without EF's retrying strategy unless a test calls `EnableSqlRetryOnFailure()`. Code that opens
  its own transaction can pass every test here and fail on the real API ([the trap][test-host]).
- **Some tests read files outside this folder**, and fail when they cannot find the repository
  root. An edit to a runbook under `docs/runbooks/`, to `docs/api/openapiv1.json` or to the Bruno
  collection under `tests/api-collection/` can fail a test here.
- **A test that a decision record names holds that decision.** Deleting or renaming it changes
  the record, not only the suite ([Tests](../../../docs/engineering-practices.md#tests)).

## See Also

- [The API](../../src/AzureBank.Api/README.md), the host these tests start.
- [The decision records](../../../docs/adr/README.md), which name the tests that hold them.

[test-host]: ../../../docs/engineering-traps.md#the-test-host-is-not-the-production-host-the-retrying-strategy-is-opt-in
