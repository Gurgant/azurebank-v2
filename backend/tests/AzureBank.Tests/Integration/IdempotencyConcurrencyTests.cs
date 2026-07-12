using AzureBank.Tests.Fixtures;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Concurrency smoke of the idempotency guarantee on the default (EF
/// InMemory) host: the composite-PK claim is enforced atomically by the
/// InMemory store too (verified empirically: 1 winner out of 30 parallel
/// duplicate-PK inserts), so this runs everywhere including plain CI.
///
/// The authoritative proof on real SQL Server semantics lives in
/// <see cref="IdempotencySqlServerConcurrencyTests"/>.
///
/// Each proof runs 3 rounds to pin determinism, not luck.
/// </summary>
public class IdempotencyConcurrencyTests : IntegrationTestBase
{
    private const int Parallelism = 24;
    private const int Rounds = 3;

    private readonly ITestOutputHelper _output;

    public IdempotencyConcurrencyTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
        : base(factory)
    {
        _output = output;
    }

    [Fact]
    public async Task ParallelIdenticalTransfers_ExecuteExactlyOnce()
    {
        for (var round = 1; round <= Rounds; round++)
        {
            _output.WriteLine($"--- round {round}/{Rounds} ---");
            await IdempotencyConcurrencyProof.RunTransferProofAsync(
                Client, Parallelism, _output.WriteLine);
        }
    }

    [Fact]
    public async Task ParallelIdenticalDeposits_ExecuteExactlyOnce()
    {
        for (var round = 1; round <= Rounds; round++)
        {
            _output.WriteLine($"--- round {round}/{Rounds} ---");
            await IdempotencyConcurrencyProof.RunDepositProofAsync(
                Client, Parallelism, _output.WriteLine);
        }
    }
}
