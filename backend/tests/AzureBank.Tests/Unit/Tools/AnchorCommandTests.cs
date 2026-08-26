using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// The anchor mode's own behaviour — which it shipped without.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ THIS FILE EXISTS BECAUSE THERE WAS NOTHING HERE. The command that writes to the audit store
/// merged with zero tests: <c>RunAsync</c> is <c>internal</c> and the tool's project carried no
/// <c>InternalsVisibleTo</c>, so the suite could not reach it even to try. The chain underneath was
/// tested thoroughly; the thing an operator actually runs was not.
/// </para>
/// <para>
/// What that cost, measured rather than supposed: against an unreachable database the command exited
/// <b>4</b> — this tool's word for "the command line was wrong" — and printed a raw .NET stack trace,
/// while <c>verify</c> answered the same outage with 3 and a sentence. Two commands, one outage, two
/// numbers, and the wrong one is the number automation reads.
/// </para>
/// </remarks>
public class AnchorCommandTests : IDisposable
{
    private const string ChainKey = "anchor-command-tests-chain-key-0123456789abc";
    private const string AnchorKey = "anchor-command-tests-anchor-key-9876543210xyz";

    private readonly ServiceProvider _services;
    private readonly AzureBankDbContext _context;

    public AnchorCommandTests()
    {
        var options = Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey });
        var chain = new AuditChain(options, NullLogger<AuditChain>.Instance);

        _context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            timeProvider: null,
            auditChain: chain);

        var collection = new ServiceCollection();
        collection.AddSingleton<IOptions<AuditOptions>>(options);
        collection.AddSingleton<IAuditChain>(chain);
        collection.AddSingleton<IAuditAnchorChain>(new AuditAnchorChain(options));
        collection.AddSingleton(_context);
        _services = collection.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task WriteRowsAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _context.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTime.UtcNow,
                Event = $"AnchorCommandEvent{i}",
                Outcome = AuditOutcome.Succeeded,
                ActorUserId = Guid.NewGuid(),
                RowHash = string.Empty,
            });
            await _context.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task AnchoringAnIntactChain_WritesARecordAndPrintsThePairToKeep()
    {
        var (exitCode, lines) = await AnchorCommand.RunAsync(_services, CancellationToken.None);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(VerifyCommand.NothingToVerify, "the table is empty, which is not intact");

        await WriteRowsAsync(3);
        (exitCode, lines) = await AnchorCommand.RunAsync(_services, CancellationToken.None);
        text = string.Join(" ", lines);

        exitCode.Should().Be(VerifyCommand.Intact);
        (await _context.Set<AuditAnchor>().CountAsync()).Should().Be(2, "the empty run recorded too");

        /*
          THE PAIR, NOT THE COUNTER. The printed advice has to carry BOTH numbers, because this
          deployment holds the anchor key: a bare counter can be regrown by re-running this command,
          with genuine authentication codes and valid links every step of the way, until it reaches
          whatever number is on the operator's paper. The covered sequence cannot be regrown downward.

          FALSIFIED by printing only the anchor number.
        */
        text.Should().Contain("through sequence", "the covered sequence is the half that cannot be regrown");
        text.Should().Contain(
         "CANNOT REACH", "and it has to say to keep the pair off this machine");
    }

    [Fact]
    public async Task AnchoringABrokenChain_RecordsAGapMarkerAndSaysTheChainIsBroken()
    {
        await WriteRowsAsync(2);
        var row = await _context.AuditEvents.OrderBy(e => e.Sequence).FirstAsync();
        row.Detail = "altered";
        await _context.SaveChangesAsync();

        var (exitCode, lines) = await AnchorCommand.RunAsync(_services, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.Broken, "the verdict about the chain is still the verdict");
        string.Join(" ", lines).Should().Contain("GAP MARKER");

        var record = await _context.Set<AuditAnchor>().SingleAsync();
        record.Kind.Should().Be(AuditAnchorKind.GapMarker);
        record.CoveredRowCount.Should().BeNull("a marker asserts coverage of nothing");
    }

    [Fact]
    public async Task AnUnauthenticatableChain_RefusesToAppendAndSaysNothingWasWritten()
    {
        /*
          REFUSING IS THE POINT. Extending a chain this run cannot vouch for would make the new
          record's link assert that everything beneath it was there and fine — the one claim a run is
          least entitled to make, and the one that launders the damage.

          THE SECOND RUN HOLDS A DIFFERENT ANCHOR KEY, which is how an operator actually reaches
          this state: a key rotated, a wrong environment, a second machine. Tampering with the record
          instead is refused by the insert-only guard before it ever reaches the chain -- that guard
          defends against our own code, and the attack it does NOT stop is pinned on SQL Server,
          where it can be performed with a connection rather than a change tracker.

          FALSIFIED by dropping the VerifyChainAsync guard from the command: the append succeeds and
          this reddens.
        */
        await WriteRowsAsync(2);
        await AnchorCommand.RunAsync(_services, CancellationToken.None);

        var stranger = Options.Create(new AuditOptions
        {
            ChainKey = ChainKey,
            AnchorKey = "a-completely-different-anchor-key-0123456789",
        });
        var collection = new ServiceCollection();
        collection.AddSingleton<IOptions<AuditOptions>>(stranger);
        collection.AddSingleton<IAuditChain>(
            new AuditChain(stranger, NullLogger<AuditChain>.Instance));
        collection.AddSingleton<IAuditAnchorChain>(new AuditAnchorChain(stranger));
        collection.AddSingleton(_context);
        using var services = collection.BuildServiceProvider();

        var (exitCode, lines) = await AnchorCommand.RunAsync(services, CancellationToken.None);

        exitCode.Should().Be(
            AnchorCommand.NotRecorded,
            "there was a verdict and nothing came of it, which is neither success nor no-verdict");

        var text = string.Join(" ", lines);
        text.Should().Contain("NOT RECORDED");
        text.Should().Contain(
            "NOT checked", "a record this run cannot apply is unchecked, never proved wrong");
        (await _context.Set<AuditAnchor>().CountAsync()).Should().Be(1, "nothing was appended");
    }

    [Fact]
    public async Task AMissingAnchorKey_IsRefusedBeforeAnythingIsRead()
    {
        var options = Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = "too-short" });
        var collection = new ServiceCollection();
        collection.AddSingleton<IOptions<AuditOptions>>(options);
        collection.AddSingleton<IAuditChain>(
            new AuditChain(options, NullLogger<AuditChain>.Instance));
        collection.AddSingleton<IAuditAnchorChain>(new AuditAnchorChain(options));
        collection.AddSingleton(_context);
        using var services = collection.BuildServiceProvider();

        var (exitCode, lines) = await AnchorCommand.RunAsync(services, CancellationToken.None);

        exitCode.Should().Be(VerifyCommand.Misconfigured);
        string.Join(" ", lines).Should().Contain("Audit:AnchorKey");
    }

    [Fact]
    public async Task AStoreThatCannotBeRead_IsNOTAUsageError()
    {
        /*
          THE DEFECT THIS FILE WAS WRITTEN FOR, and it was measured before it was fixed: against an
          unreachable instance this command exited 4 — "the command line was wrong" — and dumped a
          .NET stack trace, because only DbUpdateException and OperationCanceledException were caught
          and a SqlException escaped into System.CommandLine's handler.

          Here the store is disposed rather than unreachable, which produces the same shape through a
          different exception: something the command did not anticipate, thrown while reading.

          FALSIFIED by removing the catch-all: the exception escapes RunAsync and this test fails
          with the exception rather than an assertion, which is the honest signal.
        */
        _context.Dispose();

        var (exitCode, lines) = await AnchorCommand.RunAsync(_services, CancellationToken.None);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(
            VerifyCommand.Misconfigured,
            "verify answers this outage with 3, and one outage must not have two numbers");
        exitCode.Should().NotBe(VerifyCommand.UsageError, "nothing was wrong with the command line");
        text.Should().Contain("NO VERDICT");
        text.Should().Contain(
            "NOT a statement about the chain", "an outage must never read as a verdict");
    }
}
