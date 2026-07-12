namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Serializes the SQL-Server-gated proof classes (xUnit runs classes of the
/// same collection sequentially).
///
/// Why: each proof creates its own WebApplicationFactory that runs
/// db.Database.Migrate() against AZUREBANK_TEST_SQLSERVER. On a FRESH
/// database (the CI case) parallel Migrate() calls race on CREATE
/// DATABASE/TABLE and the losing hosts die at startup. Sequential execution
/// makes the first factory apply the schema and the rest no-op.
/// </summary>
[CollectionDefinition(Name)]
public class SqlServerProofsCollection
{
    public const string Name = "SqlServerProofs";
}
