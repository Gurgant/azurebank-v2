using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The <c>anchor</c> command against a real database, in the two cases only one can make: a record
/// deleted on the connection, and two runs that both go for the next number.
/// </summary>
/// <remarks>
/// <para>
/// NEITHER RUNS ON THE INMEMORY PROVIDER. A deletion there goes through the context, which refuses
/// it; the deletion that matters is a DELETE by whoever holds a connection. And a double launch is
/// stopped by the table's key, which the in-memory provider does not have in the same form.
/// </para>
/// <para>
/// Both clear <c>AuditAnchors</c> and <c>AuditEvents</c> first, as <c>AuditAnchorSqlServerTests</c>
/// does: the classes of this collection run one after another on one database.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class AnchorCommandSqlServerTests : IDisposable
{
    private CustomWebApplicationFactory? _factory;

    public void Dispose() => _factory?.Dispose();

    private IServiceProvider CreateSqlServices()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _ = _factory.CreateClient(); // forces the host to build and the database to migrate
        return _factory.Services;
    }

    private static async Task ClearAndWriteRowsAsync(IServiceProvider services, int count)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM [AuditAnchors]");
        await context.Database.ExecuteSqlRawAsync("DELETE FROM [AuditEvents]");

        for (var i = 0; i < count; i++)
        {
            context.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTime.UtcNow,
                Event = $"AnchorCommandProof{i}",
                Outcome = AuditOutcome.Succeeded,
                ActorUserId = Guid.NewGuid(),
                RowHash = string.Empty,
            });
            await context.SaveChangesAsync();
        }
    }

    private static async Task<List<long>> AnchorNumbersAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await context.Set<AuditAnchor>().AsNoTracking()
            .OrderBy(a => a.AnchorSequence).Select(a => a.AnchorSequence).ToListAsync();
    }

    [SqlServerFact]
    public async Task ARecordDeletedOnTheConnection_StopsTheNextAnchor_AndIsNamedAsARemoval()
    {
        // The one move the anchor chain is built to make loud. The survivors still authenticate,
        // so a run that looked at the newest record alone would extend a chain with a hole in it.
        // Measured 2026-10-12: exit 6, "Anchor 2 is missing", records 1 and 3 left as they were.
        var services = CreateSqlServices();
        await ClearAndWriteRowsAsync(services, 3);
        for (var i = 0; i < 3; i++)
        {
            var (arranged, _) = await AnchorCommand.RunAsync(services, CancellationToken.None);
            arranged.Should().Be(VerifyCommand.Intact, "ARRANGE: three anchors over an intact chain");
        }

        using (var scope = services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var deleted = await context.Database.ExecuteSqlRawAsync("DELETE FROM [AuditAnchors] WHERE [AnchorSequence] = 2");
            deleted.Should().Be(1, "ARRANGE: the record in the middle is gone, past the context's own refusal");
        }

        var (exitCode, lines) = await AnchorCommand.RunAsync(services, CancellationToken.None);
        var text = string.Join("\n", lines);

        using var all = new AssertionScope();
        exitCode.Should().Be(AnchorCommand.NotRecorded);
        lines[0].Should().Be("NOT RECORDED: the existing anchor chain did not verify, so nothing was appended.");
        text.Should().Contain("Records verified before the break: 1");
        text.Should().Contain("Anchor 2 is missing: the next record read is 3.");
        text.Should().Contain("A REMOVED RECORD IS THE ONE MOVE THIS CHAIN EXISTS TO MAKE LOUD");
        (await AnchorNumbersAsync(services)).Should().Equal([1L, 3L], "nothing was appended over the hole");
    }

    [SqlServerFact]
    public async Task TwoRunsForTheSameNumber_OneRecordsIt_AndTheOtherSaysSoAndWritesNothing()
    {
        // The tail is read without a lock, on purpose: an operator's tool fails fast and is run
        // again. So two runs can both build number 2, and the table's key lets one in. Here the
        // second run starts and finishes between the first one's read of the tail and its write.
        // Measured 2026-10-12: the run that lost exits 6 and says another run wrote the next anchor first; one
        // record numbered 2.
        var services = CreateSqlServices();
        await ClearAndWriteRowsAsync(services, 3);
        var (first, _) = await AnchorCommand.RunAsync(services, CancellationToken.None);
        first.Should().Be(VerifyCommand.Intact, "ARRANGE: anchor 1 exists");

        (int ExitCode, string[] Lines) other = default;
        using var scope = services.CreateScope();
        var collection = new ServiceCollection();
        collection.AddSingleton(scope.ServiceProvider.GetRequiredService<IOptions<AuditOptions>>());
        collection.AddSingleton(scope.ServiceProvider.GetRequiredService<IAuditChain>());
        collection.AddSingleton(scope.ServiceProvider.GetRequiredService<AzureBankDbContext>());
        collection.AddSingleton<IAuditAnchorChain>(new LetsAnotherRunInAfterTheTailIsRead(
            scope.ServiceProvider.GetRequiredService<IAuditAnchorChain>(),
            async () => other = await AnchorCommand.RunAsync(services, CancellationToken.None)));
        await using var slow = collection.BuildServiceProvider();

        var (exitCode, lines) = await AnchorCommand.RunAsync(slow, CancellationToken.None);

        using var all = new AssertionScope();
        other.ExitCode.Should().Be(VerifyCommand.Intact, "the run that got there first recorded its anchor");
        string.Join("\n", other.Lines ?? []).Should().Contain("ANCHOR 2 recorded.");
        exitCode.Should().Be(AnchorCommand.NotRecorded, "not a verdict about the chain, and not an outage");
        lines[0].Should().Be("NOT RECORDED: another run wrote the next anchor first.");
        (await AnchorNumbersAsync(services)).Should().Equal([1L, 2L], "one record for one number");
    }

    /// <summary>
    /// The real anchor chain, with another run let in between this run's read of the tail and what
    /// it does with it.
    /// </summary>
    private sealed class LetsAnotherRunInAfterTheTailIsRead(IAuditAnchorChain inner, Func<Task> anotherRun)
        : IAuditAnchorChain
    {
        public async Task<AuditAnchor?> ReadTailAsync(DbContext context, CancellationToken cancellationToken = default)
        {
            var tail = await inner.ReadTailAsync(context, cancellationToken);
            await anotherRun();
            return tail;
        }

        public AuditAnchorCheck Check(AuditAnchor anchor) => inner.Check(anchor);

        public AuditAnchor Build(AuditChainVerification verification, AuditAnchor? tail, DateTime createdAtUtc) =>
            inner.Build(verification, tail, createdAtUtc);

        public Task<AuditAnchorChainVerification> VerifyChainAsync(
            DbContext context, CancellationToken cancellationToken = default) =>
            inner.VerifyChainAsync(context, cancellationToken);
    }
}
