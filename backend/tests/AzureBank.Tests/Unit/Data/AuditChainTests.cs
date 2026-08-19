using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// The tamper-evidence of the audit trail (ADR-0044), exercised on the InMemory provider.
/// </summary>
/// <remarks>
/// <para>
/// BE PRECISE ABOUT WHAT THESE CAN AND CANNOT PROVE, because the whole reason the hash chain was
/// built before the SQL Server ledger is that the chain is application code and therefore testable
/// HERE, where ~585 of this project's 623 tests run. What lives here: the chain links, an altered
/// row is caught, a removed row is caught, and the verification counts what it read.
/// </para>
/// <para>
/// What does NOT live here, and must not be claimed from here: that concurrent writers cannot fork
/// the chain. Nothing on InMemory serialises, so a green test here would say nothing about it. That
/// property belongs to the SQL Server proofs — and asserting it from an InMemory test would be
/// exactly the "green and false" state this project treats as the worst possible.
/// </para>
/// </remarks>
public class AuditChainTests : IDisposable
{
    private const string TestKey = "unit-test-audit-chain-key-0123456789abcdef";

    private readonly AzureBankDbContext _context;
    private readonly AuditChain _chain;

    public AuditChainTests()
    {
        _chain = new AuditChain(Options.Create(new AuditOptions { ChainKey = TestKey }));
        _context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            timeProvider: null,
            auditChain: _chain);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private AuditEvent NewEvent(string name) => new()
    {
        Id = Guid.CreateVersion7(),
        OccurredAt = DateTime.UtcNow,
        Event = name,
        Outcome = AuditOutcome.Succeeded,
        ActorUserId = Guid.NewGuid(),
        RowHash = string.Empty,
    };

    private async Task<List<AuditEvent>> WriteAsync(params string[] names)
    {
        foreach (var name in names)
        {
            _context.AuditEvents.Add(NewEvent(name));
            // One SaveChanges per row: the chain must survive being built across separate units of
            // work, which is how it is actually written in production.
            await _context.SaveChangesAsync();
        }

        /*
          ORDERED BY Sequence, NOT BY Id, and this is the exact trap the production code was already
          corrected for — repeated here, and caught by a suite that was red on 2 runs out of 3.
          Guid.CreateVersion7() is not monotonic WITHIN a millisecond, and these three saves land in
          the same one on a warm machine, so "rows[0]" ordered by Id was sometimes the second row
          written. Sequence is the order the chain is defined over; it is the only correct key here.
        */
        return await _context.AuditEvents.AsNoTracking().OrderBy(e => e.Sequence).ToListAsync();
    }

    [Fact]
    public async Task SavingAnEvent_FillsTheHash_AndLinksItToThePreviousRow()
    {
        var rows = await WriteAsync("First", "Second", "Third");

        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(r => r.RowHash.Length == 64, "the hash is HMAC-SHA256 as lowercase hex");

        // The first row starts the chain; every later row carries its predecessor's hash.
        rows[0].PreviousHash.Should().BeNull("nothing precedes the first row");
        rows[1].PreviousHash.Should().Be(rows[0].RowHash);
        rows[2].PreviousHash.Should().Be(rows[1].RowHash);

        var verification = await _chain.VerifyAsync(_context);
        verification.IsIntact.Should().BeTrue(because: verification.Reason);
        verification.Verified.Should().Be(3, "a verification that read nothing would also report intact");
    }

    [Fact]
    public async Task AlteringARow_BreaksItsOwnHash()
    {
        await WriteAsync("First", "Second", "Third");

        /*
          The tampering an attacker with database access would attempt: change what an event says
          while leaving the chain structure alone. The row still links correctly to its predecessor —
          only its own content no longer matches its hash.
        */
        var tracked = await _context.AuditEvents.OrderBy(e => e.Sequence).Skip(1).FirstAsync();
        tracked.Event = "SomethingElseEntirely";
        await _context.SaveChangesAsync();

        var verification = await _chain.VerifyAsync(_context);

        verification.IsIntact.Should().BeFalse("the row no longer hashes to what is stored beside it");
        verification.Reason.Should().Contain("altered after it was written");
    }

    [Fact]
    public async Task RemovingARow_BreaksTheLinkOfTheNextOne()
    {
        await WriteAsync("First", "Second", "Third");

        /*
          The other tampering that matters, and the one a per-row checksum alone cannot catch:
          deleting an event outright. Every surviving row still hashes correctly — what gives it away
          is that the third row records a predecessor that is no longer there.
        */
        var middle = await _context.AuditEvents.OrderBy(e => e.Sequence).Skip(1).FirstAsync();
        _context.AuditEvents.Remove(middle);
        await _context.SaveChangesAsync();

        var verification = await _chain.VerifyAsync(_context);

        verification.IsIntact.Should().BeFalse("the survivor still points at a row that has been removed");
        verification.Reason.Should().Contain("deleted, reordered, or inserted");
    }

    [Fact]
    public async Task AChainWrittenWithADifferentKey_DoesNotVerify()
    {
        await WriteAsync("First", "Second");

        /*
          Why the hash is KEYED rather than a bare digest. Every field of an audit row is
          enumerable — two Guids, a timestamp, an event name from a list of seventeen — so anyone
          holding the table could recompute an unkeyed hash after editing a row and leave no trace.
          A verifier that does not hold the key must be unable to confirm the chain, which is the
          same thing as saying a forger without the key cannot produce one.
        */
        var wrongKey = new AuditChain(
            Options.Create(new AuditOptions { ChainKey = "a-different-key-entirely-0123456789abcdef" }));

        var verification = await wrongKey.VerifyAsync(_context);

        verification.IsIntact.Should().BeFalse("the key is what makes the hash unforgeable");
    }

    [Fact]
    public async Task SavingAnAuditRow_WithoutAChain_IsRefusedRatherThanWrittenUnhashed()
    {
        /*
          The loud failure that keeps this honest. A context built without an IAuditChain used to be
          able to write a row with an empty RowHash — which would read as audited and prove nothing.
          Refusing is the whole point, so it is pinned here.
        */
        using var unchained = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        unchained.AuditEvents.Add(NewEvent("Orphan"));

        var act = () => unchained.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*hash chain cannot be computed*");
    }

    [Fact]
    public async Task AContextWithoutAChain_StillSavesEverythingElse()
    {
        // The negative control for the guard above: it must refuse audit rows WITHOUT breaking the
        // fourteen contexts constructed by hand in tests that never touch AuditEvents.
        using var unchained = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        unchained.Accounts.Add(new Account
        {
            Id = Guid.CreateVersion7(),
            UserId = Guid.NewGuid(),
            AccountNumber = "AB-1234-5678-01",
            Name = "Ordinary",
            Type = AccountType.Savings,
            Balance = 0,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 1], // InMemory needs it set, as AccountServiceTests does
            User = null!, // Navigation not needed here, same as AccountServiceTests' helper
        });

        var saved = await unchained.SaveChangesAsync();

        saved.Should().Be(1, "the guard is about audit rows, not about every save");
    }
}
