extern alias seeder;

using ConnectionTarget = seeder::AzureBank.Seeder.Commands.ConnectionTarget;

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
///
/// IT ALSO SKIPS WHEN THE VARIABLE NAMES AN AZURE SQL SERVER. Several proofs create a database of
/// their own on the server the variable names, through EF, and drop it afterwards. On Azure SQL
/// each of those would be a new paid database at the service's default size. The rule is the
/// Seeder's own (<c>ConnectionTarget</c>, by the server's name), so the proofs refuse what
/// <c>seed</c> and <c>reset</c> refuse. Before, the variable only had to be set.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public const string ConnectionStringVariable = "AZUREBANK_TEST_SQLSERVER";

    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionStringVariable);

    public SqlServerFactAttribute()
    {
        Skip = SkipReason(ConnectionString);
    }

    /// <summary>
    /// Why a SQL Server proof must not run on <paramref name="connectionString"/>, or null when it
    /// may: the sentence a skipped test shows. Reads the string and opens nothing.
    /// </summary>
    public static string? SkipReason(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return $"Requires SQL Server - set {ConnectionStringVariable} to a connection string to run.";
        }

        var target = ConnectionTarget.Read(connectionString);
        return target.IsAzureSql
            ? $"{ConnectionStringVariable} names an Azure SQL server ({target.Server}). These proofs create and "
                + "drop databases on the server it names, and on Azure SQL each one is a paid database: "
                + "use LocalDB or a container."
            : null;
    }
}
