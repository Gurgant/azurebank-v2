using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
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
        _chain = new AuditChain(Options.Create(new AuditOptions { ChainKey = TestKey }), NullLogger<AuditChain>.Instance);
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

    /// <summary>
    /// Two rows whose hashes are written down, so the SHAPE of the payload cannot change quietly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-field theory on SQL Server proves each field is IN the payload. It is blind to two
    /// things, and both are the kind of change a refactor makes without noticing: SWAPPING two
    /// same-typed entries in the join, and DELETING the <c>"v2"</c> prefix. In each case the writer
    /// and the verifier move together, every field is still hashed, and every field case stays
    /// green. Frozen literals are what notice.
    /// </para>
    /// <para>
    /// It is also the only guard on the <c>PreviousHash</c> line. A tamper case cannot reach it —
    /// <c>VerifyAsync</c> returns <c>LinkBroken</c> before the hash is computed — but row two here
    /// carries a non-empty <c>PreviousHash</c>, so deleting that line from the join moves this
    /// literal. <b>Delete this test and two payload lines lose their only guard.</b>
    /// </para>
    /// <para>
    /// EVERY FIELD IS FIXED, DISTINCT AND NON-EMPTY on purpose: a swap of two empty fields is
    /// invisible, and a swap of two equal ones is too. Two separate <c>SaveChangesAsync</c> calls
    /// with DISTINCT <c>OccurredAt</c> values, because the pending-rows query orders by
    /// <c>OccurredAt</c> — equal values leave the order undefined and these literals would flake.
    /// </para>
    /// <para>
    /// The literals pin the payload shape AND <see cref="TestKey"/>, since the hash is an HMAC over
    /// it. Rule for whoever turns them red: <b>changing the key means re-deriving both literals;
    /// changing the payload ON PURPOSE means bumping the prefix past <c>v2</c> and updating both in
    /// the same commit.</b> That is what the prefix is for. Obtain them by RUNNING the test and
    /// pasting what it printed — never by recomputing the payload inside the test, because then a
    /// reorder gets "fixed" in both copies by one edit and the guard evaporates.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheHashedPayloadHasAKnownAnswer_SoItsShapeCannotChangeQuietly()
    {
        var first = new AuditEvent
        {
            Id = Guid.Parse("11111111-1111-4111-8111-111111111111"),
            OccurredAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Event = "FirstEvent",
            Outcome = AuditOutcome.Succeeded,
            ActorUserId = Guid.Parse("22222222-2222-4222-8222-222222222222"),
            SubjectType = "AccountOne",
            SubjectId = Guid.Parse("33333333-3333-4333-8333-333333333333"),
            TraceId = "trace-for-the-first-row",
            Detail = "detail-for-the-first-row",
            RowHash = string.Empty,
        };
        _context.AuditEvents.Add(first);
        await _context.SaveChangesAsync();

        var second = new AuditEvent
        {
            Id = Guid.Parse("44444444-4444-4444-8444-444444444444"),
            OccurredAt = new DateTime(2026, 6, 7, 8, 9, 10, DateTimeKind.Utc),
            Event = "SecondEvent",
            Outcome = AuditOutcome.Refused,
            ActorUserId = Guid.Parse("55555555-5555-4555-8555-555555555555"),
            SubjectType = "AccountTwo",
            SubjectId = Guid.Parse("66666666-6666-4666-8666-666666666666"),
            TraceId = "trace-for-the-second-row",
            Detail = "detail-for-the-second-row",
            RowHash = string.Empty,
        };
        _context.AuditEvents.Add(second);
        await _context.SaveChangesAsync();

        second.PreviousHash.Should().Be(
            first.RowHash, "row two must carry row one's hash, or this test guards nothing");

        first.RowHash.Should().Be(
            "b2f91735f5846d4e078cad27fdf8d20b73c5a0f3f2bccaa00b5cd3d342c376f6",
            "the payload's shape and the key together produce exactly this value");
        second.RowHash.Should().Be(
            "51625bb81b8d175f1ab88d928a398a9291a33a360eb16483be5a26f67d14048e",
            "and this one, which also covers the PreviousHash line no tamper case can reach");
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
    public async Task TheReportedRangeIsWhatTheWALKSaw_NotWhatTheTableHolds()
    {
        /*
          THE DISCRIMINATOR IS A BROKEN CHAIN. The verifier prints the sequence range beside the row
          count so an operator can compare them; that comparison is worthless if the two come from
          different reads. They used to: the tool asked the database for MIN and MAX before walking,
          so a row committed in between was counted and fell outside the range -- 101 rows verified
          over a range ending at 100.

          Taking the range from the table would be indistinguishable from taking it from the walk on
          an intact chain, because they agree. On a BROKEN one they do not: the walk stops at row 3
          while the table still holds 5, so a reported high of 3 can only have come from the walk.
        */
        await WriteAsync("First", "Second", "Third", "Fourth", "Fifth");

        // Mutate the TRACKED instance, as AlteringARow_BreaksItsOwnHash does; calling Update() on a
        // second instance of an already-tracked row throws before the test can prove anything.
        var third = await _context.AuditEvents.OrderBy(e => e.Sequence).Skip(2).FirstAsync();
        var first = await _context.AuditEvents.OrderBy(e => e.Sequence).FirstAsync();
        third.Event = "SomethingElseEntirely";
        await _context.SaveChangesAsync();

        var verification = await _chain.VerifyAsync(_context);

        verification.IsIntact.Should().BeFalse("row three was altered");
        verification.Verified.Should().Be(2, "it checked two rows before reaching the altered one");
        verification.HighestSequence.Should().Be(
            third.Sequence,
            "the range must end where the WALK stopped, not where the table ends -- the table still "
            + "holds five rows, and reporting 5 here would be the stale-bounds defect returning");
        verification.LowestSequence.Should().Be(
            first.Sequence, "and start at the first row it actually read");

        (await _context.AuditEvents.CountAsync()).Should().Be(
            5, "the control: the table is longer than the walk, which is what makes this test bite");
    }

    [Fact]
    public async Task TruncatingTheTAIL_IsNotDetected_AndThisPinsTheLimit()
    {
        /*
          THE LIMIT OF THE PROPERTY, ASSERTED SO IT CANNOT BE OVERSTATED AGAIN. The test above shows
          an INTERIOR deletion is caught, and it is caught by the NEXT row pointing at a predecessor
          that is gone. Delete from the END and there is no next row: the surviving prefix is
          perfectly self-consistent, every link holds, every hash matches, and VerifyAsync reports
          intact. Nothing in the chain records how many rows there should have been.

          So a hash chain proves rows were not ALTERED or REMOVED FROM THE MIDDLE. It does not prove
          none were removed from the end. That needs an external witness — an anchored head, a
          counter someone else keeps — which this system does not have yet.

          This test exists because the runbook claimed the stronger property in writing. It is
          deliberately asserting the UNCOMFORTABLE direction: if someone later anchors the head, this
          goes red, and the documentation it protects gets updated with it.
        */
        await WriteAsync("First", "Second", "Third");

        (await _chain.VerifyAsync(_context)).Verified.Should().Be(3, "three rows were written");

        var tail = await _context.AuditEvents.OrderByDescending(e => e.Sequence).FirstAsync();
        _context.AuditEvents.Remove(tail);
        await _context.SaveChangesAsync();

        var verification = await _chain.VerifyAsync(_context);

        verification.IsIntact.Should().BeTrue(
            "MEASURED, and it is the honest answer rather than the comfortable one: a truncated "
            + "prefix links perfectly, so the chain cannot tell that anything is missing");
        verification.Verified.Should().Be(
            2,
            "the ONLY trace is that the count dropped — which is evidence to somebody who wrote the "
            + "previous count down somewhere else, and to nobody who did not");
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
            Options.Create(new AuditOptions { ChainKey = "a-different-key-entirely-0123456789abcdef" }),
            NullLogger<AuditChain>.Instance);

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
