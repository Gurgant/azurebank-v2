using System.Data.Common;
using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The audit chain against a real database, where a row can be made unreadable and the read of
/// the tail can fail or be abandoned: what the walk says, what the verifier exits with, and what
/// is logged.
/// </summary>
/// <remarks>
/// <para>
/// A ROW THAT CANNOT BE READ. <c>Outcome</c> is a string in the table and an enum in the code, so
/// one UPDATE on the connection can store a value EF will not materialise. The in-memory provider
/// hands back the object that was saved and cannot be put in that state. Left to escape, the
/// failure reached the verifier as "the store could not be read", exit 3, "NOT a statement about
/// the chain": one statement that muzzles the tool, on a column that is inside the hashed payload.
/// </para>
/// <para>
/// THE READ OF THE TAIL. A save whose tail read fails is refused, and says so at Error under
/// <c>AuditChainUnavailable</c>, the event an operator alerts on. A save the caller abandoned is
/// refused too and says nothing: counted with the others, a browser closing a tab would raise the
/// alert that exists for an audit store that is down.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class AuditChainUnreadableRowAndTailSqlServerTests : IDisposable
{
    private CustomWebApplicationFactory? _factory;

    public void Dispose() => _factory?.Dispose();

    private IServiceProvider CreateSqlServices(Action<CustomWebApplicationFactory>? configure = null)
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        configure?.Invoke(_factory);
        _ = _factory.CreateClient(); // forces the host to build and the database to migrate
        return _factory.Services;
    }

    private static AuditEvent NewEvent(string name) => new()
    {
        Id = Guid.CreateVersion7(),
        OccurredAt = DateTime.UtcNow,
        Event = name,
        Outcome = AuditOutcome.Succeeded,
        ActorUserId = Guid.NewGuid(),
        RowHash = string.Empty,
    };

    private static async Task ClearAndWriteRowsAsync(IServiceProvider services, int count)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM [AuditAnchors]");
        await context.Database.ExecuteSqlRawAsync("DELETE FROM [AuditEvents]");
        for (var i = 0; i < count; i++)
        {
            context.AuditEvents.Add(NewEvent($"UnreadableRowProof{i}"));
            await context.SaveChangesAsync();
        }
    }

    private static async Task<int> CountRowsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AzureBankDbContext>().AuditEvents.AsNoTracking().CountAsync();
    }

    private static async Task StoreAnOutcomeThatIsNoOutcomeAsync(IServiceProvider services, long sequence)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var changed = await context.Database.ExecuteSqlRawAsync(
            "UPDATE [AuditEvents] SET [Outcome] = N'Vanished' WHERE [Sequence] = {0}", sequence);
        changed.Should().Be(1, "ARRANGE: one row now holds an outcome the code has no name for");
    }

    [SqlServerFact]
    public async Task AFIRSTRowThatCannotBeRead_IsABrokenChain_NeitherAnOutageNorAnEmptyTable()
    {
        // The case that moved the muzzle instead of removing it: with nothing read yet, a verdict
        // with no broken sequence is "intact, zero rows", and the verifier says NOTHING TO VERIFY.
        // Measured 2026-10-12: not intact, kind Unreadable, 0 verified, position 1; `verify` exits 1.
        var services = CreateSqlServices();
        await ClearAndWriteRowsAsync(services, 3);
        await StoreAnOutcomeThatIsNoOutcomeAsync(services, sequence: 1);

        AuditChainVerification walk;
        using (var scope = services.CreateScope())
        {
            walk = await scope.ServiceProvider.GetRequiredService<IAuditChain>()
                .VerifyAsync(scope.ServiceProvider.GetRequiredService<AzureBankDbContext>());
        }

        var (exitCode, lines) = await VerifyCommand.RunAsync(services, CancellationToken.None);
        var text = string.Join("\n", lines);

        using var all = new AssertionScope();
        walk.IsIntact.Should().BeFalse();
        walk.Kind.Should().Be(AuditChainBreakKind.Unreadable);
        walk.Verified.Should().Be(0);
        walk.FirstBrokenSequence.Should().Be(1);
        walk.Reason.Should().Contain("could not be read at all").And.Contain("Vanished");

        exitCode.Should().Be(VerifyCommand.Broken, "a stored value that contradicts the schema is a rewritten row");
        exitCode.Should().NotBe(VerifyCommand.Misconfigured, "3 says the chain was not judged, and sends the operator to a connection string");
        exitCode.Should().NotBe(VerifyCommand.NothingToVerify, "three rows are in the table");
        lines[0].Should().Be("CHAIN BROKEN at sequence 1.");
        text.Should().NotContain("NOT a statement about the chain");
    }

    [SqlServerFact]
    public async Task ALATERRowThatCannotBeRead_IsABreakAfterTheRowsThatWereRead()
    {
        // The walk knows how far it got: the rows before the unreadable one are counted, and the
        // break is placed after the last one read.
        // Measured 2026-10-12: 2 verified, range 1 to 2, position 3; `verify` exits 1.
        var services = CreateSqlServices();
        await ClearAndWriteRowsAsync(services, 4);
        await StoreAnOutcomeThatIsNoOutcomeAsync(services, sequence: 3);

        AuditChainVerification walk;
        using (var scope = services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            walk = await scope.ServiceProvider.GetRequiredService<IAuditChain>().VerifyAsync(context);

            // The reader is closed on this exit too: the next query on the same context answers.
            (await context.AuditEvents.AsNoTracking().CountAsync(e => e.Sequence <= 2)).Should().Be(2);
        }

        var (exitCode, lines) = await VerifyCommand.RunAsync(services, CancellationToken.None);

        using var all = new AssertionScope();
        walk.Kind.Should().Be(AuditChainBreakKind.Unreadable);
        walk.Verified.Should().Be(2);
        walk.LowestSequence.Should().Be(1);
        walk.HighestSequence.Should().Be(2);
        walk.FirstBrokenSequence.Should().Be(3);
        walk.Reason.Should().StartWith("The row after sequence 2 could not be read at all");
        exitCode.Should().Be(VerifyCommand.Broken);
        lines[0].Should().Be("CHAIN BROKEN at sequence 3.");
        string.Join("\n", lines).Should().Contain("Rows verified before the break: 2");
    }

    [SqlServerFact]
    public async Task ASynchronousSaveWhoseTailReadFails_WritesNoRow_AndSaysSoUnderTheEventOperatorsAlertOn()
    {
        // The synchronous funnel reports as the asynchronous one does. No money service saves
        // synchronously today; "not used today" is not a bound.
        // Measured 2026-10-12: InvalidOperationException out of SaveChanges, 2 rows before and
        // after, one Error line naming AuditChainUnavailable and "1 pending audit row(s)".
        var tail = new ActsOnTheTailRead();
        var services = CreateSqlServices(factory =>
        {
            factory.CaptureLog();
            factory.AddInterceptor(tail);
        });
        await ClearAndWriteRowsAsync(services, 2);

        tail.Then = _ => throw new InvalidOperationException("Injected failure: the tail could not be read.");
        Exception? refused;
        using (var scope = services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            context.AuditEvents.Add(NewEvent("RefusedSynchronously"));
            refused = Record.Exception(() => context.SaveChanges());
        }

        tail.Then = null;
        var unavailable = _factory!.CapturedLog
            .Where(line => line.Contains(SecurityEvents.AuditChainUnavailable, StringComparison.Ordinal))
            .ToList();

        using var all = new AssertionScope();
        tail.Seen.Should().BeGreaterThan(0, "ARRANGE: the save did read the tail, synchronously");
        refused.Should().BeOfType<InvalidOperationException>("the failure goes on: the row and the action it describes do not happen");
        (await CountRowsAsync(services)).Should().Be(2, "nothing was written");
        unavailable.Should().ContainSingle()
            .Which.Should().StartWith("[Error] ").And.Contain("1 pending audit row(s)").And.Contain("ON THIS ATTEMPT");
    }

    [SqlServerFact]
    public async Task ASaveAbandonedDuringTheTailRead_WritesNoRow_AndIsNotLoggedAsAnAuditFailure()
    {
        // A SILENCE IS ONLY EVIDENCE BESIDE A LINE THAT WAS DUE AND CAME. So the same host, the
        // same sink and the same interceptor first fail a tail read, which must log, and then
        // abandon one, which must not.
        // Measured 2026-10-12: one line after the failure, still one after the abandoned save.
        var tail = new ActsOnTheTailRead();
        var services = CreateSqlServices(factory =>
        {
            factory.CaptureLog();
            factory.AddInterceptor(tail);
        });
        await ClearAndWriteRowsAsync(services, 2);

        int Unavailable() => _factory!.CapturedLog.Count(
            line => line.Contains(SecurityEvents.AuditChainUnavailable, StringComparison.Ordinal));

        tail.Then = _ => throw new InvalidOperationException("Injected failure: the tail could not be read.");
        Exception? failed;
        using (var scope = services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            context.AuditEvents.Add(NewEvent("RefusedByAFailure"));
            failed = await Record.ExceptionAsync(() => context.SaveChangesAsync());
        }

        var afterTheFailure = Unavailable();

        using var gone = new CancellationTokenSource();
        tail.Then = token =>
        {
            // The caller goes away while the tail is being read, and the read ends as a cancelled
            // read does.
            gone.Cancel();
            token.ThrowIfCancellationRequested();
        };
        Exception? abandoned;
        using (var scope = services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            context.AuditEvents.Add(NewEvent("AbandonedByTheCaller"));
            abandoned = await Record.ExceptionAsync(() => context.SaveChangesAsync(gone.Token));
        }

        tail.Then = null;

        using var all = new AssertionScope();
        failed.Should().BeOfType<InvalidOperationException>();
        afterTheFailure.Should().Be(1, "the line was due and came: the sink hears this event on this host");
        tail.Seen.Should().Be(2, "ARRANGE: both saves reached the read of the tail");
        abandoned.Should().BeAssignableTo<OperationCanceledException>("the save still does not happen");
        Unavailable().Should().Be(1, "a caller that went away is not an audit store that is down");
        (await CountRowsAsync(services)).Should().Be(2, "neither row was written");
    }

    /// <summary>
    /// Runs <see cref="Then"/> when the audit chain reads its tail, synchronously or not, before
    /// the statement is sent. Matched on the lock hint, as <c>SlowAuditTailInterceptor</c> is: no
    /// other read of the table carries it.
    /// </summary>
    private sealed class ActsOnTheTailRead : DbCommandInterceptor
    {
        private int _seen;

        /// <summary>What to do at the next tail read; null lets it through.</summary>
        public Action<CancellationToken>? Then { get; set; }

        /// <summary>Tail reads on which <see cref="Then"/> ran. 0 means the test proved nothing.</summary>
        public int Seen => Volatile.Read(ref _seen);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Act(command, CancellationToken.None);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Act(command, cancellationToken);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Act(DbCommand command, CancellationToken cancellationToken)
        {
            if (Then is not { } then
                || !command.CommandText.Contains("[AuditEvents] WITH (UPDLOCK", StringComparison.Ordinal))
            {
                return;
            }

            Interlocked.Increment(ref _seen);
            then(cancellationToken);
        }
    }
}
