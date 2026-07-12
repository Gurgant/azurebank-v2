using AzureBank.Tests.Fixtures;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// THE authoritative concurrency proof of ADR-0009, on REAL SQL Server:
/// unique-key violations under parallel INSERT, concurrency-token fencing
/// and transactional atomicity behave here exactly as in production —
/// the InMemory provider only approximates them.
///
/// Gated by AZUREBANK_TEST_SQLSERVER (see SqlServerFactAttribute):
/// - locally: LocalDB
/// - CI: mssql service container (backend-sql job in ci.yml)
///
/// The factory is created lazily per test with SetConnectionString, which
/// migrates the database and seeds the Identity roles.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class IdempotencySqlServerConcurrencyTests : IDisposable
{
    private const int Parallelism = 24;
    private const int Rounds = 3;

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public IdempotencySqlServerConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SqlServerFact]
    public async Task ParallelIdenticalTransfers_ExecuteExactlyOnce_OnSqlServer()
    {
        var client = CreateSqlClient();
        for (var round = 1; round <= Rounds; round++)
        {
            _output.WriteLine($"--- round {round}/{Rounds} (SQL Server) ---");
            await IdempotencyConcurrencyProof.RunTransferProofAsync(
                client, Parallelism, _output.WriteLine);
        }
    }

    [SqlServerFact]
    public async Task ParallelIdenticalDeposits_ExecuteExactlyOnce_OnSqlServer()
    {
        var client = CreateSqlClient();
        for (var round = 1; round <= Rounds; round++)
        {
            _output.WriteLine($"--- round {round}/{Rounds} (SQL Server) ---");
            await IdempotencyConcurrencyProof.RunDepositProofAsync(
                client, Parallelism, _output.WriteLine);
        }
    }

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
