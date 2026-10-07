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
/// Each proof runs 3 rounds. In every round the same request is sent once
/// more after the parallel ones have been answered, and must come back as a
/// replay: the winner's status, the Idempotency-Replayed header and exactly
/// the winner's response text.
/// (Until 2026-10-06 this said the 3 rounds were there "to pin determinism,
/// not luck". They did not pin the comparison of a replay with the winner's
/// answer: a round whose parallel requests brought back no replay compared
/// none. Measured that day on this host: with that comparison's expected
/// text altered on purpose, a test whose three rounds had no replay passed.)
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
