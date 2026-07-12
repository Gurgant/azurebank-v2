namespace AzureBank.Tests.Fixtures;

/// <summary>
/// A [Fact] that only runs when a real SQL Server connection string is
/// provided via the AZUREBANK_TEST_SQLSERVER environment variable.
///
/// Local (LocalDB):
///   AZUREBANK_TEST_SQLSERVER="Server=(localdb)\MSSQLLocalDB;Database=AzureBankTests;Trusted_Connection=True;TrustServerCertificate=True"
/// CI (ubuntu): a mssql service container (see .github/workflows/ci.yml).
///
/// Used for proofs that depend on real database semantics (locking, unique
/// key violations under concurrency, concurrency tokens in transactions) —
/// the EF InMemory provider only approximates them.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public const string ConnectionStringVariable = "AZUREBANK_TEST_SQLSERVER";

    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionStringVariable);

    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            Skip = $"Requires SQL Server - set {ConnectionStringVariable} to a connection string to run.";
        }
    }
}
