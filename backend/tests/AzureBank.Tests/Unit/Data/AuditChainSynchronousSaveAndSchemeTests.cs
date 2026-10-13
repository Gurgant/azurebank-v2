using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// Two roads through the audit chain that no test took: a row saved through the SYNCHRONOUS
/// <c>SaveChanges</c>, and a row whose payload version this build cannot render.
/// </summary>
/// <remarks>
/// On the in-memory provider, as <c>AuditChainTests</c> is, and for the same reason it gives: the
/// hash, the link and the verdict are deterministic and need no database. What a real one adds
/// (the lock on the tail, a row that will not materialise) is in the SQL Server proofs.
/// </remarks>
public sealed class AuditChainSynchronousSaveAndSchemeTests : IDisposable
{
    private const string ChainKey = "sync-save-and-scheme-tests-chain-key-0123456";
    private const string AnchorKey = "sync-save-and-scheme-tests-anchor-key-987654";

    private readonly IOptions<AuditOptions> _options =
        Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey });

    private readonly AuditChain _chain;
    private readonly AzureBankDbContext _context;

    public AuditChainSynchronousSaveAndSchemeTests()
    {
        _chain = new AuditChain(_options, NullLogger<AuditChain>.Instance);
        _context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            timeProvider: null,
            auditChain: _chain);
    }

    public void Dispose() => _context.Dispose();

    private static AuditEvent NewEvent(string name) => new()
    {
        Id = Guid.CreateVersion7(),
        OccurredAt = DateTime.UtcNow,
        Event = name,
        Outcome = AuditOutcome.Succeeded,
        ActorUserId = Guid.NewGuid(),
        RowHash = string.Empty,
    };

    [Fact]
    public async Task ARowSavedSynchronously_IsNumberedAndLinkedToTheRowBefore_WhicheverSaveWroteThat()
    {
        // SaveChanges(bool) is a public entry point, and every audited unit of work can reach it.
        // A row it wrote without reading the tail would be a second "first row": number 1 again,
        // following nothing, and the walk would call the chain broken at the next honest row.
        // Measured 2026-10-12: sequences 1, 2, 3; each row follows the hash of the one before.
        _context.AuditEvents.Add(NewEvent("SavedAsynchronously"));
        await _context.SaveChangesAsync();
        _context.AuditEvents.Add(NewEvent("SavedSynchronously"));
        _context.SaveChanges();
        _context.AuditEvents.Add(NewEvent("SavedSynchronouslyAgain"));
        _context.SaveChanges();

        var rows = await _context.AuditEvents.AsNoTracking().OrderBy(e => e.Sequence).ToListAsync();
        var walk = await _chain.VerifyAsync(_context);

        using var all = new AssertionScope();
        rows.Select(r => r.Sequence).Should().Equal(1L, 2L, 3L);
        rows.Select(r => r.Event).Should().Equal("SavedAsynchronously", "SavedSynchronously", "SavedSynchronouslyAgain");
        rows[0].PreviousHash.Should().BeNull("the first row follows nothing");
        rows[1].PreviousHash.Should().Be(rows[0].RowHash, "the synchronous save read the tail the asynchronous one left");
        rows[2].PreviousHash.Should().Be(rows[1].RowHash, "and then its own");
        rows.Should().OnlyContain(r => r.RowHash.Length == 64, "every row is hashed, not left with the placeholder");
        walk.IsIntact.Should().BeTrue();
        walk.Verified.Should().Be(3);
    }

    [Fact]
    public async Task ARowThatDeclaresAVersionThisBuildCannotRender_IsABreak_AndIsNotCheckedAsIfItWereCurrent()
    {
        // The version is inside the hashed payload. A row that names another one is either from a
        // newer build or was overwritten, and recomputing it under the current rendering would
        // give a mismatch that reads as a rewritten row. It is reported as NOT CHECKED, at its own
        // sequence, with the rows before it counted.
        // Measured 2026-10-12: verified 1, broken at 2, kind UnknownScheme, version "v9".
        for (var i = 0; i < 3; i++)
        {
            _context.AuditEvents.Add(NewEvent($"SchemeEvent{i}"));
            await _context.SaveChangesAsync();
        }

        var second = await _context.AuditEvents.OrderBy(e => e.Sequence).Skip(1).FirstAsync();
        second.PayloadVersion = "v9";
        await _context.SaveChangesAsync();

        var walk = await _chain.VerifyAsync(_context);

        using var all = new AssertionScope();
        walk.IsIntact.Should().BeFalse("a walk that skips a row it cannot check has not verified the chain");
        walk.Kind.Should().Be(AuditChainBreakKind.UnknownScheme);
        walk.FirstBrokenSequence.Should().Be(2);
        walk.Verified.Should().Be(1, "the row before it was checked and is good");
        walk.PayloadVersion.Should().Be("v9");
        walk.Reason.Should().Contain("declares payload version 'v9'").And.Contain("its hash was NOT checked");
        walk.TailRowHash.Should().BeNull("a broken walk names no tail for an anchor to commit to");
    }

    [Fact]
    public async Task Verify_OverARowOfAnUnknownVersion_ExitsOne_AndSaysTheRowWasNotChecked()
    {
        // The same table through the command an operator runs.
        // Measured 2026-10-12: exit 1, "CHAIN BROKEN at sequence 2."
        for (var i = 0; i < 3; i++)
        {
            _context.AuditEvents.Add(NewEvent($"SchemeEvent{i}"));
            await _context.SaveChangesAsync();
        }

        var second = await _context.AuditEvents.OrderBy(e => e.Sequence).Skip(1).FirstAsync();
        second.PayloadVersion = "v9";
        await _context.SaveChangesAsync();

        var collection = new ServiceCollection();
        collection.AddSingleton(_options);
        collection.AddSingleton<IAuditChain>(_chain);
        collection.AddSingleton<IAuditAnchorChain>(new AuditAnchorChain(_options));
        collection.AddSingleton(_context);
        await using var provider = collection.BuildServiceProvider();

        var (exitCode, lines) = await VerifyCommand.RunAsync(provider, CancellationToken.None);
        var text = string.Join("\n", lines);

        using var all = new AssertionScope();
        exitCode.Should().Be(VerifyCommand.Broken);
        lines[0].Should().Be("CHAIN BROKEN at sequence 2.");
        text.Should().Contain("Rows verified before the break: 1");
        text.Should().Contain("declares payload version 'v9'");
        text.Should().Contain("NOT checked");
    }
}
