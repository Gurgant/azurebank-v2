using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AzureBank.AuditVerifier.Commands;
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

    /// <summary>Named, so a second context can reach the SAME store — which is what lets a test
    /// write through a different key than the one it verifies with.</summary>
    private readonly string _storeName = Guid.NewGuid().ToString();

    public AuditChainTests()
    {
        _chain = new AuditChain(Options.Create(new AuditOptions { ChainKey = TestKey }), NullLogger<AuditChain>.Instance);
        _context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(_storeName)
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

        /*
          THESE TWO LITERALS MOVED WHEN THE PAYLOAD GAINED ITS VERSION AND KEY-IDENTITY ELEMENTS, and
          the movement IS the guard working: element one is now the row's stored version rather than
          a literal, and element two is the key id. Both were re-derived by RUNNING this test and
          pasting what it measured -- never by recomputing the payload here, because a test that
          computes its own expectation agrees with any bug the production code has.
        */
        first.RowHash.Should().Be(
            "6bdc834571631f871ffa2a56b70b7d19c0dfcfb88a6df7045899bc438e303f43",
            "the payload's shape and the key together produce exactly this value");
        second.RowHash.Should().Be(
            "9f1eff44cb788bdabe99cab84dee32b29ee67394c7967877bbb7f62cc19c8bac",
            "and this one, which also covers the PreviousHash line no tamper case can reach");
    }

    [Fact]
    public void TheKeyIdIsDerivedFromTheKey_AndHasAKnownAnswer()
    {
        /*
          THE ONLY GUARD ON THE DERIVATION, and it needs one because all three of its inputs are
          frozen the moment the first v3 row is written: the algorithm, the domain string and the
          truncation length are inside that row's hashed payload. Change any of them later and every
          stored KeyId stops matching its key, which reports as a break on rows nobody touched.

          Measured by RUNNING this, like the payload vector above -- not recomputed here, because a
          test that derives its own expectation agrees with whatever the production code does.
        */
        AuditChain.DeriveKeyId(TestKey).Should().Be(
            "b78e425e698034a4",
            "the domain string, the algorithm and the truncation length together produce exactly this");

        AuditChain.DeriveKeyId("a-different-key").Should().NotBe(
            AuditChain.DeriveKeyId(TestKey),
            "an identifier that does not change with the key identifies nothing");

        AuditChain.DeriveKeyId(TestKey).Should().HaveLength(16, "the column is nchar(16)")
            .And.MatchRegex("^[0-9a-f]+$", "lowercase hex, like every other digest this repo stores");
    }

    [Fact]
    public async Task TheWriterAssignsTheVersionAndKeyId_NotTheCaller()
    {
        /*
          THE COLUMN IS THE VERIFIER'S INSTRUCTION FOR HOW TO READ THE ROW, so it must come from the
          component that renders the payload. A caller that could set it could ship a row declaring a
          scheme it was not written under -- and the promise that the column and the prefix cannot
          disagree is empty unless exactly one authority writes the string.

          FALSIFIED by guarding either assignment in Link() with an "if it is already set" check:
          this goes red, and nothing else does.
        */
        var row = NewEvent("CallerTriedToChooseTheScheme");
        row.PayloadVersion = "v2";
        row.KeyId = "0000000000000000";

        _context.AuditEvents.Add(row);
        await _context.SaveChangesAsync();

        row.PayloadVersion.Should().Be("v3", "the chain overwrites what the caller asked for");
        row.KeyId.Should().Be(
            AuditChain.DeriveKeyId(TestKey), "and it names the key that actually wrote the row");

        (await _chain.VerifyAsync(_context)).IsIntact.Should().BeTrue();
    }

    [Fact]
    public async Task ALegacyRowWithNoKeyIdentity_StillVerifies_UnderTheFoundingKey()
    {
        /*
          THE TEST THE DEFERRED DOCUMENT PROMISED WHOEVER SHIPPED THIS, and the whole point of storing
          the version: a row written under the OLD scheme must keep verifying, unchanged, forever.
          Without the dispatch it is recomputed as v3, mismatches, and reports as tampering -- which
          is the defect this change removes.

          The row is written normally and then demoted, because Pending() filters EntityState.Added,
          so Link() never touches a Modified row and the demotion survives the save.

          FALSIFIED by deleting the legacy arm of the dispatch: this reddens on its own.
        */
        // Every field is fixed, because the legacy hash below is a FROZEN literal and NewEvent's
        // Guid.CreateVersion7 and DateTime.UtcNow would make one impossible.
        var row = new AuditEvent
        {
            Id = Guid.Parse("77777777-7777-4777-8777-777777777777"),
            OccurredAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            Event = "WrittenBeforeKeyIdentityExisted",
            Outcome = AuditOutcome.Succeeded,
            ActorUserId = Guid.Parse("88888888-8888-4888-8888-888888888888"),
            // EVERY FIELD DISTINCT AND NON-EMPTY, for the reason the sibling vector states: a swap
            // of two EMPTY fields is invisible to a hash. This is now the only computation of the
            // legacy rendering left in the suite, and that rendering is frozen forever, so it is
            // also the only thing standing between a future edit to that arm and silent breakage of
            // every historical row.
            SubjectType = "LegacySubjectType",
            SubjectId = Guid.Parse("99999999-9999-4999-8999-999999999999"),
            TraceId = "legacy-trace-id",
            Detail = "legacy-detail",
            RowHash = string.Empty,
        };
        _context.AuditEvents.Add(row);
        await _context.SaveChangesAsync();

        /*
          The demotion survives the save because Pending() filters EntityState.Added, so Link() never
          touches a Modified row.

          THE HASH IS COMPUTED OUTSIDE .NET, deliberately. Asking the production hasher for it would
          make this test agree with whatever that hasher does, including a broken legacy arm -- which
          is the one thing it exists to catch. This value came from an independent HMAC-SHA256 over
          the legacy payload rendered by hand:
            v2|7777...7777|1|639081975670000000|WrittenBeforeKeyIdentityExisted|Succeeded|
            8888...8888|LegacySubjectType|9999...9999|legacy-trace-id||legacy-detail
          the same technique ADR-0044 used to find the ISO-8601-versus-ticks defect.
        */
        row.PayloadVersion = "v2";
        row.KeyId = null;
        row.RowHash = "115e82f19c2fcd4a87c9f10edde251b62ca0131af94cbfce25bf437d3e1f29db";
        await _context.SaveChangesAsync();

        var verification = await _chain.VerifyAsync(_context);

        verification.IsIntact.Should().BeTrue(
            "a row that records no key identity is read under the founding key, which is what "
            + "Audit:ChainKey still is");
        verification.Verified.Should().Be(1, "and it was actually walked, not skipped");
    }

    [Fact]
    public async Task ALegacyRowCarryingAKeyIdentity_IsABreak()
    {
        /*
          Without this, the NULL rule is a hole the width of the column: the legacy payload has no
          key-identity element, so an id sitting on such a row is unhashed, unexplained, and nothing
          wrote it legitimately.

          FALSIFIED by removing that arm of the scheme check.
        */
        var row = NewEvent("LegacyRowWithAnIdItCannotHave");
        _context.AuditEvents.Add(row);
        await _context.SaveChangesAsync();

        row.PayloadVersion = "v2";
        row.KeyId = AuditChain.DeriveKeyId(TestKey);
        await _context.SaveChangesAsync();

        var verification = await _chain.VerifyAsync(_context);

        verification.IsIntact.Should().BeFalse();
        verification.Kind.Should().Be(AuditChainBreakKind.UnknownScheme);
    }

    [Fact]
    public async Task ACurrentRowWithNoKeyIdentity_IsABreak()
    {
        var row = NewEvent("CurrentRowStrippedOfItsIdentity");
        _context.AuditEvents.Add(row);
        await _context.SaveChangesAsync();

        row.KeyId = null;
        await _context.SaveChangesAsync();

        var verification = await _chain.VerifyAsync(_context);

        verification.IsIntact.Should().BeFalse(
            "the current payload hashes its key identity, so a row missing one was modified");
        verification.Kind.Should().Be(AuditChainBreakKind.UnknownScheme);
    }

    [Fact]
    public async Task AnUnproducibleKeyIdentity_IsABreak_AndNotAConfigurationNote()
    {
        /*
          THE ANTI-MUZZLE, and the reason UnknownScheme is a BREAK rather than a remark. Tamper a row
          AND overwrite its key identity with something no key produces: if "I cannot check this"
          were a note, the verdict would soften from tampering to housekeeping, and the column meant
          to strengthen the chain would have weakened it.

          FALSIFIED by making the scheme check skip the row, or by letting it report IsIntact.
        */
        var row = NewEvent("TamperedAndThenMasked");
        _context.AuditEvents.Add(row);
        await _context.SaveChangesAsync();

        row.Detail = "altered after the fact";
        row.KeyId = "ffffffffffffffff";
        await _context.SaveChangesAsync();

        var verification = await _chain.VerifyAsync(_context);

        verification.IsIntact.Should().BeFalse("silence is not a verdict this walk is allowed to reach");
        verification.Kind.Should().Be(AuditChainBreakKind.UnknownScheme);
        verification.RecordedKeyId.Should().Be("ffffffffffffffff");
        verification.ConfiguredKeyId.Should().Be(
            AuditChain.DeriveKeyId(TestKey), "the verdict names both, so a caller never parses a sentence");
    }

    [Fact]
    public async Task ATamperedRowAtSequence1_CarriesItsIdentityIntoTheOPERATORSVERDICT()
    {
        /*
          THE SEAM TEST, AND IT EXISTS BECAUSE ITS ABSENCE ALREADY COST SOMETHING. Every other test
          of the operator report BUILDS AuditChainVerification BY HAND, so the suite was green over a
          branch that could never run: the walk returned HashMismatch without the three diagnostic
          fields, they defaulted to null, and the arm that exonerates a confirmed key was dead code
          while a runbook, a code comment and a test all asserted it worked.

          So this one takes the verdict FROM THE PRODUCER and hands it to the consumer untouched.
          The assertion that would have caught the defect is the PayloadVersion one: a hand-built
          record cannot fail it, and the real one did.

          FALSIFIED by dropping the last three arguments at the HashMismatch return in AuditChain --
          this reddens on the PayloadVersion assertion, and again on the printed text.
        */
        var row = NewEvent("TamperedAtSequenceOne");
        _context.AuditEvents.Add(row);
        await _context.SaveChangesAsync();

        row.Detail = "altered after it was written";   // KeyId deliberately left alone
        await _context.SaveChangesAsync();

        var verification = await _chain.VerifyAsync(_context);

        verification.Kind.Should().Be(AuditChainBreakKind.HashMismatch);
        verification.FirstBrokenSequence.Should().Be(1);
        verification.PayloadVersion.Should().Be(
            "v3", "the verdict has to carry what the row declared, or the tool cannot tell the "
            + "operator which of two opposite things happened");
        verification.RecordedKeyId.Should().Be(AuditChain.DeriveKeyId(TestKey));

        var (exitCode, lines) = VerifyCommand.Report(verification, 1, 1);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(VerifyCommand.Broken);
        text.Should().NotContain(
            "Confirm the key before escalating",
            "the row named the key this verification holds, so it was confirmed one check earlier");
        text.Should().Contain("WRITE", "which makes this an escalation");
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
          none were removed from the end. That needs an EXTERNAL witness: a count somebody else
          keeps.

          ⚠️ AN ANCHORED TAIL IS NOT THAT BY ITSELF, and this comment used to list the two as one
          thing, then say the system had neither. AuditAnchors has since shipped, and it lives in the
          same database as the rows it counts — remove a suffix from both chains and each verifies
          perfectly, because each links backwards only. What is still missing is the SOMEBODY ELSE,
          and two deferred controls supply it at different layers: engine enforcement of the write,
          and time issued from outside. ADR-0044 carries both and why neither replaces the other.

          This test exists because the runbook claimed the stronger property in writing. It is
          deliberately asserting the UNCOMFORTABLE direction.

          ⚠️ IT IS NOT A TRIPWIRE, though this comment used to promise it was — "if someone later
          anchors the head, this goes red, and the documentation it protects gets updated with it".
          Wrong twice. The word is TAIL: `head` is sequence 1, the row truncation spares. And this
          asserts on what AuditChain.VerifyAsync returns, while an anchor check needs a token store
          and a pinned trust root, so it lands in AzureBank.AuditVerifier instead — the tail gets
          anchored and this stays green. ✅ It did, and it does: migration 20260826135621
          AddAuditAnchors landed and this test never moved.

          ⚠️ DO NOT DELETE IT THEN — the easier mistake to make from here. Green is the CORRECT
          answer at this layer: the chain alone cannot see a truncated
          tail, and that is precisely why an anchor is wanted. This is the test that objects if
          somebody teaches VerifyAsync to consult anchors from behind IAuditChain. What changes is
          SCOPE, not existence — "IsNotDetected" is a claim about the SYSTEM, and after anchoring the
          system detects it one layer up FOR ROWS A TRUSTED ANCHOR COVERS. Not for the rest:
          truncation of rows written since the last anchor is exactly as invisible as it is today,
          which is why the interval is the guarantee. Note the condition: no anchor here is TRUSTED
          yet, so that sentence is still about a system this one is not.

          ✅ THE DAY CAME AND NOTHING ANNOUNCED IT — which is precisely what that line was warning
          about, so it is answered here rather than deleted. The verifier-layer tests exist
          (AnchorCommandTests, AuditAnchorSqlServerTests), and ADR-0044 and
          docs/deferred/anchoring-the-audit-trail.md both record that an anchor record now exists and
          does NOT close the end. ✅ docs/runbooks/audit-chain-unavailable.md now says
          plenty about it — the anchor appears throughout and the UNCOVERED WINDOW has its own
          section, landed 2026-08-28. That was the last item on this list and it is closed. Noting it
          here rather than deleting the line, because the line was a promise about a document and the
          promise was kept.
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
    public async Task DeletingTheOLDESTRows_IsLOUD_WhichIsWhyRetentionCannotPurgeThisTable()
    {
        /*
          THE THIRD SHAPE, AND THE ONE A RETENTION POLICY ACTUALLY TAKES. The two tests around this
          one cover deletion from the MIDDLE (caught) and from the END (invisible). Neither is what a
          retention rule asks for. A retention rule says "remove what is older than N years", which is
          a PREFIX deletion — the oldest rows, from sequence 1 upward — and until now nothing pinned
          what this chain does with that.

          It is caught, and it is caught LOUDLY: the lowest surviving row records a predecessor hash,
          the walk starts with `previous = null`, and the two do not match. AuditChain says so in as
          many words — "expected to follow '(start of chain)'" — and reports LinkBroken, the same
          verdict a tamper gets. There is no separate vocabulary for a lawful deletion, and there
          should not be: a chain that could tell an authorised removal from an unauthorised one would
          have to trust whoever declared it authorised.

          ⚠️ SO THE COLLISION IS REAL AND THIS TEST IS WHERE IT IS MEASURED. AMLR Art. 77 carries a
          deletion duty at expiry, and this table is built so that discharging it here would be
          indistinguishable from an attack.

          ⚠️ THIS COMMENT USED TO ADD "and the answer is erasure upstream, where a real DELETE is
          possible". That was wrong twice and D6 in ADR-0044 now says so: pseudonymous ids are still
          personal data while anybody can link them, and EnforceTransactionImmutability REFUSES to
          delete the ledger row the money events point at. The duty is undischarged, not relocated.

          ⚠️ AND THAT IS WHY THIS TEST EXISTS RATHER THAN A COMMENT. The policy says "never purge the
          trail". The cheap way to break that policy is not malice, it is somebody in a year reading
          "retention: 5 years" and writing a job that deletes old audit rows, believing it safe
          because the rows are old.

          ⚠️ BUT ONLY IF THE JOB LEAVES A SURVIVOR, and the first version of this comment claimed
          more than that — it said the test goes red the moment such a job runs. Raised in review and
          measured: delete EVERY row and the chain reports intact, because there is no surviving row
          left to point at a missing predecessor. The partial purge is loud and the total one is
          silent, which is the opposite of the comfortable assumption that a bigger deletion is
          easier to see. PurgingTheWHOLETable_IsSILENT_WhichIsTheOtherHalfOfWhyRetentionCannotUseIt
          is the other half, and the two are only useful together.

          FALSIFIED by removing the TAIL instead of the head: IsIntact goes back to true, which is
          the sibling test above and the reason both are needed.
        */
        await WriteAsync("First", "Second", "Third");

        (await _chain.VerifyAsync(_context)).Verified.Should().Be(3, "three rows were written");

        var oldest = await _context.AuditEvents.OrderBy(e => e.Sequence).FirstAsync();
        _context.AuditEvents.Remove(oldest);
        await _context.SaveChangesAsync();

        var verification = await _chain.VerifyAsync(_context);

        verification.IsIntact.Should().BeFalse(
            "a prefix deletion leaves the lowest surviving row pointing at a hash that is gone, and "
            + "the walk starts expecting no predecessor at all");
        verification.Kind.Should().Be(
            AuditChainBreakKind.LinkBroken,
            "the same verdict a tamper gets — the chain has no vocabulary for an authorised removal, "
            + "and inventing one would mean trusting whoever declared it authorised");
        verification.Reason.Should().Contain(
            "start of chain",
            "the message names the shape, which is what an operator needs to tell this from a "
            + "mid-table deletion");
    }

    [Fact]
    public async Task PurgingTheWHOLETable_IsSILENT_WhichIsTheOtherHalfOfWhyRetentionCannotUseIt()
    {
        /*
          THE HOLE IN THE TEST ABOVE, AND IT WAS RAISED IN REVIEW. That test deletes ONE of three rows
          and concludes that a purge job would be caught. It would not, necessarily: a retention job
          says "delete everything older than N years", and on a table where everything is older than
          N years that deletes EVERY row. No survivor is left to point at a missing predecessor, so
          there is nothing for the walk to catch.

          Measured here rather than reasoned about, because the comfortable assumption is that a
          bigger deletion is easier to see. It is the opposite: the PARTIAL purge is loud and the
          TOTAL one is silent. The chain can only speak through a surviving row.

          ⚠️ THIS IS WHY "NEVER PURGE THIS TABLE" IS A POLICY AND NOT A CONTROL. Nothing in the chain
          enforces it. A partial purge produces a verdict indistinguishable from tampering; a complete
          purge produces the verdict a fresh installation produces. Neither is a retention mechanism,
          and the difference between them is not a safety margin — it is which mistake happens to be
          made.

          The operator-facing tool is one layer better and it is worth being exact about where: it
          refuses to render an empty table as green, exiting NothingToVerify rather than Intact. That
          separates zero from non-zero. It does not separate "purged" from "new", because nothing in
          this database can.
        */
        await WriteAsync("First", "Second", "Third");

        (await _chain.VerifyAsync(_context)).Verified.Should().Be(3, "three rows were written");

        _context.AuditEvents.RemoveRange(await _context.AuditEvents.ToListAsync());
        await _context.SaveChangesAsync();

        var verification = await _chain.VerifyAsync(_context);

        verification.IsIntact.Should().BeTrue(
            "MEASURED, and it is the uncomfortable answer: with no surviving row there is no broken "
            + "link to find, so the chain reports the same thing it reports for a table nobody has "
            + "written to yet");
        verification.Verified.Should().Be(
            0,
            "the ONLY trace a complete purge leaves is the count, which is evidence to somebody "
            + "holding a number from elsewhere and to nobody else — the same limit the tail "
            + "truncation test records, arrived at from the other end");
    }

    /// <summary>
    /// The ring as it stands after a rotation: the current key is <see cref="RotatedKey"/>,
    /// <see cref="TestKey"/> is retired at <paramref name="lastSequence"/>, and TestKey is the
    /// founding key — which is what it is, since it wrote everything the fixture writes.
    /// </summary>
    /// <remarks>
    /// Extracted because the three call sites were byte-identical 141-column lines, 41 past the
    /// corpus wrap of 100. They were introduced on 32770df and survived four rounds of review because each
    /// round measured only its own working-tree diff and never the branch.
    /// </remarks>
    private static AuditChain RotatedRing(long lastSequence) => new(
        Options.Create(new AuditOptions
        {
            ChainKey = RotatedKey,
            RetiredChainKeys = [Retired(TestKey, lastSequence)],
            FoundingChainKey = TestKey,
        }),
        NullLogger<AuditChain>.Instance);

    [Fact]
    public async Task ALEGACYRowStoredBELOWSequenceONE_IsSentToESCALATION_NotToTheDESIGNATION()
    {
        /*
          A REGRESSION AGAINST main, FOUND BY ASKING WHAT ELSE REACHES AN ARM. The founding-epoch
          guard is `row.Sequence < _foundingFirstSequence`, and on a deployment that has NEVER
          rotated _foundingFirstSequence is hard-set to 1 -- one key in the ring, no designation
          configured at all. So any 'v2' row stored at 0 or below reaches the arm whose comment used
          to say the only way in was a misdesignation.

          The verdict then prescribed re-pointing Audit:FoundingChainKey, which cannot change
          anything there: with nothing retired the current key's entry is built with FirstSequence 1,
          so the founding epoch starts at 1 whatever it is pointed at, and there is no other member
          to point at. At the BASE commit the same row reached the hash and came back HashMismatch,
          which told the operator to preserve the table and escalate -- so the ring made the guidance
          worse for a row that only tampering can produce.

          Nothing here needs a key: Sequence has no CHECK constraint, and the epoch test runs before
          the hash.
        */
        await WriteAsync("Genesis");

        var planted = await _context.AuditEvents.SingleAsync();
        planted.PayloadVersion = "v2";
        planted.KeyId = null;
        planted.Sequence = 0;
        planted.PreviousHash = null;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var neverRotated = new AuditChain(
            Options.Create(new AuditOptions { ChainKey = TestKey }),
            NullLogger<AuditChain>.Instance);

        var verification = await neverRotated.VerifyAsync(_context);

        verification.Kind.Should().Be(AuditChainBreakKind.UnknownScheme);
        verification.Reason.Should().Contain(
            "Preserve the table and escalate",
            "the row can only have been inserted, so the operator's first move is preservation");
        verification.Reason.Should().NotContain(
            "The fix is Audit:FoundingChainKey",
            "⚠️ THE PRESCRIPTION THAT MADE THIS WORTH FIXING. On a ring with one key there is no "
            + "designation to re-point and the founding epoch starts at 1 either way, so the "
            + "instruction sent the operator to a setting that cannot move the verdict");
    }

    [Fact]
    public async Task ACurrentVersionRowWhoseIdentityWasBLANKED_ReadsAsREMOVED_NotAsAMissingRingEntry()
    {
        /*
          BLANK IS NOT AN IDENTITY, AND THE SWITCH USED THE RAW COLUMN. The records-none arm matched
          `row.KeyId is null`, so a 'v3' row whose identity column was emptied rather than nulled
          fell through to the default arm -- the one that says a key is missing from
          Audit:RetiredChainKeys and tells the operator to go and add it.

          That is the opposite instruction. KeyId is inside the hashed payload and nothing this
          deployment writes leaves it empty on this version, so an empty value was removed after the
          fact. The 'v2' mirror has always caught every non-null value, blanked ones included; this
          side now agrees with it.
        */
        await WriteAsync("One");

        var blanked = await _context.AuditEvents.SingleAsync();
        blanked.KeyId = "   ";
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var verification = await _chain.VerifyAsync(_context);

        verification.Kind.Should().Be(AuditChainBreakKind.UnknownScheme);
        verification.Reason.Should().Contain(
            "records none",
            "an emptied identity column is an identity that was removed, which is a modification");
        verification.Reason.Should().NotContain(
            "was never added to Audit:RetiredChainKeys",
            "⚠️ THE WRONG FIRST MOVE. The default arm sends the operator to configure a retired key "
            + "for an id that is not an id, during an incident that is a write");
    }

    private static RetiredChainKey Retired(string key, long lastSequence) =>
        new() { Key = key, LastSequence = lastSequence };

    private const string RotatedKey = "unit-test-audit-chain-key-AFTER-rotation-9876543210";

    [Fact]
    public async Task AfterARotation_HistoryStillVerifies_BecauseTheRingHoldsTheRetiredKey()
    {
        /*
          THE POINT OF #241, IN ONE ASSERTION. Before the ring, rotating Audit:ChainKey made every
          existing row unverifiable: the row names the key that wrote it, the verifier held a
          different one, and the walk broke at the lowest row with a verdict that reads exactly like
          tampering. A deployment could not rotate a key without destroying its own evidence.

          The rows are NOT rewritten and that is ratified rather than convenient: re-hashing history
          would invalidate every anchor ever issued while being, in the database, the same operation
          the anchor exists to detect. So the fix is read-side only.
        */
        await WriteAsync("Before", "Rotation");

        var rotated = RotatedRing(lastSequence: 2);

        var verification = await rotated.VerifyAsync(_context);

        verification.IsIntact.Should().BeTrue(
            "the rows name the retired key, the ring holds it, and nothing was rewritten");
        verification.Verified.Should().Be(2);
    }

    [Fact]
    public async Task WithoutRetiringTheOldKey_RotationStrandsTheHistory_WhichIsTheHONESTDefault()
    {
        // The negative half, and it must stay: a ring that quietly accepted rows it holds no key for
        // would turn "I cannot check this" into "this is fine", which is the failure the whole
        // verifier exists to avoid. Forgetting to retire a key is loud.
        await WriteAsync("Before", "Rotation");

        var rotated = new AuditChain(
            Options.Create(new AuditOptions { ChainKey = RotatedKey }),
            NullLogger<AuditChain>.Instance);

        var verification = await rotated.VerifyAsync(_context);

        verification.IsIntact.Should().BeFalse();
        verification.Kind.Should().Be(AuditChainBreakKind.UnknownScheme);
        verification.Reason.Should().Contain(
            "no key in this verification's ring has that id",
            "THE WHOLE CLAIM, not the two words it starts with. \"no key\" also appears in the "
            + "identity-less verdicts, so the short literal would have passed on a verdict about a "
            + "row this test never produces");
    }

    [Fact]
    public async Task ARetiredKeyCanREADItsRowsAndNeverWriteANewOne()
    {
        /*
          THE PROPERTY THAT MAKES A RING SAFE TO HOLD. Retiring a key keeps it in the process for
          verification; if it could also write, then possessing an old key — the exact thing rotation
          assumes has happened — would let somebody append rows that verify. Writing takes
          Audit:ChainKey and nothing else.

          BOTH HALVES ARE ASSERTED, which they were not until review pointed it out: this test named
          READ and WRITE and only ever exercised WRITE, so the half its first word claims was carried
          by the title alone.
        */
        await WriteAsync("Written", "UnderTheOldKey");

        var rotated = RotatedRing(lastSequence: 2);

        var read = await rotated.VerifyAsync(_context);
        read.IsIntact.Should().BeTrue("the READ half: a retired key still answers for its own rows");
        read.Verified.Should().Be(2);

        using var after = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            timeProvider: null,
            auditChain: rotated);

        after.AuditEvents.Add(NewEvent("After"));
        await after.SaveChangesAsync();

        var written = await after.AuditEvents.SingleAsync();
        written.KeyId.Should().Be(
            AuditChain.DeriveKeyId(RotatedKey),
            "a new row is written under the CURRENT key, never under a retired one");
        written.KeyId.Should().NotBe(AuditChain.DeriveKeyId(TestKey));

        /*
          ⚠️ THE COLUMN IS NOT THE KEY, AND UNTIL THIS ASSERTION THE WRITE HALF ONLY CHECKED THE
          COLUMN. Link() sets row.KeyId and row.RowHash from two independent expressions, so handing
          a RING MEMBER to the hasher -- the founding key, say, which here is the retired TestKey --
          would leave the identity column reading "current" and the hash taken under a retired key,
          and every assertion above would still pass. What the test's name promises is that writing
          takes Audit:ChainKey and nothing else; that is a claim about the HASH.
        */
        CurrentHash(RotatedKey, written).Should().Be(
            written.RowHash,
            "the row has to hash under the CURRENT key, which is the half the identity column cannot "
            + "show");
        CurrentHash(TestKey, written).Should().NotBe(
            written.RowHash, "and not under the retired one, whatever the column says");
    }

    [Fact]
    public async Task ARowThatLIESAboutItsKeyIsCaught_WhichIsWhyTheRingSELECTSRatherThanTRIES()
    {
        /*
          THE ASSERTION THAT SEPARATES A RING FROM A TRIAL LOOP, and the tail-anchor decision named
          the hazard before this code existed: a verifier that tries each key in turn accepts a row a
          RETIRED key could have minted at any sequence, so every rotation widens the forgery surface
          instead of narrowing it.

          Here the row was hashed under the retired key and then relabelled to claim the current one.
          A trial verifier would find a key that matches and pass it. Selection by the stated id
          cannot.

          ⚠️ WHICH CHECK CATCHES IT MOVED, AND THE OLD ANSWER IS WORTH KEEPING WRITTEN DOWN. Until
          epochs had a lower bound this failed as a HASH MISMATCH: KeyId is inside the hashed
          payload, so relabelling changed the hash the check recomputed. It now fails EARLIER, as
          UnknownScheme, because derived epochs partition the sequence space -- a row at sequence 1
          belongs to exactly one key's epoch, so naming any other key puts it outside that key's
          range before its hash is ever recomputed.

          That is strictly stronger and it makes the hash-in-payload defence the SECOND line rather
          than the first. Both still hold; only the order changed. The assertion follows the code
          rather than the other way round, and the previous expectation is recorded here because a
          reader finding UnknownScheme where a comment promised HashMismatch would otherwise suspect
          a regression.

          FALSIFIED by replacing the lookup with a loop over the ring: this reddens on the REASON.
          Both selection and a trial loop return UnknownScheme here — selection because the
          relabelled row names the current key at a sequence below that key's epoch, a trial loop
          because no key in the ring reproduces the hash — so Kind and IsIntact agree and cannot
          separate them. What a trial loop has no way to say is "epoch begins at", because it has no
          epochs, and that is what the assertion below pins.

          (This said the mutation reddens on Kind, "where selection returns HashMismatch". Selection
          returns UnknownScheme, as the paragraph eight lines above and the assertion twenty-seven
          lines below both state. The note described the world before the epoch had a lower end,
          when the relabelled row still reached the hash.)

          ⚠️ IT NO LONGER REDDENS ALONE, and the sentence here used to claim it did. That was true
          when this was the ring's only test; the epoch tests added later redden under the same
          mutation. The claim is kept in its corrected form rather than deleted because "nothing else
          does" is the kind of thing a reader relies on when deciding what a failure means.
        */
        // WriteAsync reads back with AsNoTracking, so the returned instances are detached copies.
        // Two lines here used to relabel rows[0] and then Detach it — a mutation on a copy nothing
        // reads and an attach-then-detach of an untracked object. Neither could reach the database,
        // and a reader could mistake them for the edit under test, which happens on `stored` below.
        // Raised in review on 930495f.
        var rows = await WriteAsync("Written under the old key");
        rows.Should().HaveCount(1, "the relabelling below assumes a single row to relabel");

        var rotated = RotatedRing(lastSequence: 2);

        var stored = await _context.AuditEvents.FirstAsync();
        stored.KeyId = AuditChain.DeriveKeyId(RotatedKey);
        await _context.SaveChangesAsync();

        var verification = await rotated.VerifyAsync(_context);

        verification.IsIntact.Should().BeFalse(
            "the ring holds BOTH keys, so a trial loop would have found one that matched");
        verification.Kind.Should().Be(
            AuditChainBreakKind.UnknownScheme,
            "epochs partition the sequences, so a row naming a key whose epoch does not contain it "
            + "is refused before any hash is recomputed — this was HashMismatch until the epoch "
            + "gained a lower bound, and the earlier refusal is the stronger of the two");
        verification.Reason.Should().Contain(
            "epoch begins at",
            "and the verdict has to say WHICH boundary it fell outside, or the operator cannot tell "
            + "this from a key the ring does not hold at all");
    }

    [Theory]
    [InlineData("", 5L, "is blank", "blank")]
    [InlineData(TestKey, 5L, "it is the CURRENT Audit:ChainKey", "the CURRENT key")]
    [InlineData("a-perfectly-good-retired-key-0123456789abcdef", 0L, "without that boundary",
        "unbounded")]
    [InlineData("too-short-to-be-a-key", 5L, "holds a key of", "shorter than the floor")]
    [InlineData("a-perfectly-good-retired-key-0123456789abcdef", long.MaxValue,
        "largest a sequence can be", "boundary at the top of the range")]
    [InlineData(null, 5L, "is null", "entry that binds to nothing")]
    public void ARingThatCannotMeanWhatItSays_IsRefusedAtConstruction(
        string? retired, long lastSequence, string fragment, string why)
    {
        /*
          REFUSED LOUDLY RATHER THAN DEDUPLICATED QUIETLY. Retiring the key still in use reads as "we
          rotated" while the ring holds one key, so the deployment believes its history is covered
          when nothing changed — the silent outcome, and the worst. A blank entry cannot have written
          any row, so it is a mistake and not a no-op.

          ⚠️ THE TYPE AND THE MESSAGE ARE BOTH ASSERTED, AND NEITHER USED TO BE. This asserted
          InvalidOperationException, which every one of these guards satisfies — and so does any
          OTHER InvalidOperationException the constructor might grow. Two consequences it hid:
          AuditKeyRingException, the whole point of giving anchor and export a verdict instead of an
          exit-4 stack trace, was never asserted anywhere in the suite; and the blank case is ALSO
          shorter than the floor, so deleting the blank guard entirely would have left this green on
          the length guard. A fragment unique to each guard is what makes the case isolate it — and
          THREE of these were not unique until an adversarial pass said so. "already in the ring" is
          the SHARED prefix of both arms of the duplicate-id refusal, so the CURRENT-key case was
          satisfied by the listed-twice message; "has LastSequence" opens both the below-1 guard and
          the at-MaxValue guard; and "characters" is said by TWO floor guards, the one on
          Audit:ChainKey and the one on a retired key, so the length case quoted a word that does not
          name which floor it tripped. That third one survived the pass that found the other two, and
          the sentence claiming all six were unique was written in the same commit. Each case now
          quotes something only its own guard says -- verified by testing every fragment for
          containment against all ten AuditKeyRingException messages, not by reading.
        */
        var build = () => new AuditChain(
            Options.Create(new AuditOptions
            {
                ChainKey = TestKey,
                RetiredChainKeys = [retired is null ? null! : Retired(retired, lastSequence)],
                FoundingChainKey = TestKey,
            }),
            NullLogger<AuditChain>.Instance);

        build.Should().Throw<AuditKeyRingException>(
            "a {0} retired key makes the ring claim something it cannot deliver — and the TYPE is "
            + "what the three verbs catch to answer 3 instead of exiting 4", why)
            .WithMessage($"*{fragment}*");
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("x", 1)]
    [InlineData("shorter-than-the-floor", 22)]
    public void TheCURRENTKeyIsHeldToTheSameFloorAsTheRetiredOnes(string chainKey, int length)
    {
        /*
          THE ONE MEMBER OF THE RING GOVERNED ONLY BY THE ROOTS, until this. Both composition roots
          hold Audit:ChainKey to the floor, which made it look covered — and that is exactly the
          argument the ring construction rejects for everything else it checks: "a structural rule
          enforced in one of them is a rule the other does not have."

          MEASURED before the guard: ChainKey = "" built a ring. A caller constructing AuditChain
          directly — which every test in this file does, and which the two roots are not the only
          way to do — got no check at all.
        */
        var build = () => new AuditChain(
            Options.Create(new AuditOptions { ChainKey = chainKey }),
            NullLogger<AuditChain>.Instance);

        build.Should().Throw<AuditKeyRingException>(
            "a {0}-character current key authenticates every row written from here on", length)
            .WithMessage("*Audit:ChainKey is*");
    }

    [Fact]
    public async Task ATailAtTheTOPOfTheRange_STOPSTheNextWrite_RatherThanWrappingBeneathIt()
    {
        /*
          THE OVERFLOW GUARD WENT ON THE WRONG NUMBER, and this is the number it should have been on.
          The constructor refuses a CONFIGURED LastSequence of long.MaxValue — an operator's typo.
          The STORED tail is the one an attacker with write access controls, and `++sequence` in
          Link() is unchecked.

          MEASURED before this guard: plant one row at long.MaxValue and the next honest write
          receives long.MinValue. Sequence is the column the walk ORDERS BY, so every row written
          afterwards sorts BELOW the entire history and the verdict becomes LinkBroken with nothing
          verified. One UPDATE turns the trail into something that reads as destroyed.

          Refusing is the D1 trade taken deliberately: an audit write that cannot be made honestly
          fails the business action rather than being made dishonestly. It also leaves the planted
          row in place as evidence, which a silent wrap does not.
        */
        await WriteAsync("One", "Two");

        var tail = await _context.AuditEvents.OrderByDescending(e => e.Sequence).FirstAsync();
        tail.Sequence = long.MaxValue;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        _context.AuditEvents.Add(NewEvent("TheNextHonestWrite"));

        var write = () => _context.SaveChangesAsync();

        (await write.Should().ThrowAsync<InvalidOperationException>(
            "wrapping would reorder the whole trail beneath the row that was planted"))
            .WithMessage("*largest a sequence can be*");
    }

    [Fact]
    public void TwoRetiredKeysSharingABoundary_AreRefused_BecauseTheRowsBeneathBelongToBoth()
    {
        /*
          THE GUARD WITH NO TEST UNTIL NOW, and it is the one the epoch derivation rests on. Epochs
          are derived by sorting on LastSequence and starting each one past the previous end, so two
          entries ending at the same row leave the stretch beneath claimed by both — and which one
          gets it depends on sort order between equal keys, which no configuration file states.

          Refusing is not the same as refusing a key that wrote nothing. A key that wrote nothing has
          no row naming its id, so it needs no ring entry at all; listing it is what creates the
          ambiguity. Measured while auditing: of 512 boundary triples, ZERO produce an empty epoch,
          because sorting makes equality the only reachable collision and this is what refuses it.
        */
        var second = "a-second-retired-key-0123456789abcdefghij";

        var build = () => new AuditChain(
            Options.Create(new AuditOptions
            {
                ChainKey = RotatedKey,
                RetiredChainKeys = [Retired(TestKey, 4), Retired(second, 4)],
                FoundingChainKey = TestKey,
            }),
            NullLogger<AuditChain>.Instance);

        build.Should().Throw<AuditKeyRingException>()
            .WithMessage("*Boundaries partition the sequence space*");
    }

    [Fact]
    public void ONEKeyListedTWICE_IsNotReportedAsTwoKeysColliding_BecauseTheEditDiffers()
    {
        /*
          THE SAME GUARD, THE OTHER SHAPE, AND IT USED TO GIVE THE WRONG INSTRUCTION. The boundary
          collision runs BEFORE the duplicate-id refusal, so a wholly duplicated entry -- the
          copy-paste of Audit__RetiredChainKeys__0__Key and __LastSequence into slot 1 -- reached it
          first and was reported as "two keys ending at the same row". It is one key listed twice.

          The two need OPPOSITE edits, which is why the message has to tell them apart: remove the
          duplicate entry here, versus correct a boundary there. And correcting a boundary is the
          edit every other message on this branch tells the operator to make only from the rotation
          record, so sending them to it for a copy-paste is the expensive direction to be wrong in.
        */
        var build = () => new AuditChain(
            Options.Create(new AuditOptions
            {
                ChainKey = RotatedKey,
                RetiredChainKeys = [Retired(TestKey, 4), Retired(TestKey, 4)],
                FoundingChainKey = TestKey,
            }),
            NullLogger<AuditChain>.Instance);

        build.Should().Throw<AuditKeyRingException>()
            .WithMessage("*ONE key listed twice*")
            .Which.Message.Should().NotContain(
                "two keys ending at the same row",
                "the collision wording sends the operator to edit a boundary, and the boundary is "
                + "not what is wrong here");
    }

    [Fact]
    public void TheSameRetiredKeyListedTwice_IsRefused_AndSaysSoRatherThanNamingTheCurrentOne()
    {
        /*
          THE OTHER ARM OF THE DUPLICATE-ID GUARD. Its sibling — a retired entry equal to the CURRENT
          key — has a Theory case; this one had none, and the two produce different sentences on
          purpose: one means "you believe you rotated and did not", the other means "you listed the
          same key twice". Asserting only the type would let either message serve for both.
        */
        var build = () => new AuditChain(
            Options.Create(new AuditOptions
            {
                ChainKey = RotatedKey,
                RetiredChainKeys = [Retired(TestKey, 2), Retired(TestKey, 4)],
                FoundingChainKey = TestKey,
            }),
            NullLogger<AuditChain>.Instance);

        build.Should().Throw<AuditKeyRingException>()
            .WithMessage("*the same retired key is listed twice*");
    }

    [Fact]
    public void RetiringAKeyWithoutNamingTheFoundingOne_IsRefused_BecauseTheDEFAULTWouldBeWrong()
    {
        /*
          ADR-0044 SETTLED THIS BEFORE THE RING EXISTED, and the first version of the ring did the
          forbidden thing anyway. The sentence is: "whatever adds a second key must add a ring entry
          for the FOUNDING key rather than silently re-point history at whatever is current."

          A null KeyId means no identity was RECORDED — it never means "the current key". Rows
          predating the key-identity column were written by whatever key existed then, and after a
          rotation that is not Audit:ChainKey any more. Defaulting to the current key would
          re-attribute those rows at the moment of rotation and then report every one of them as
          tampered, which is the exact failure the design exists to prevent.

          So the designation is required as soon as it can be wrong, and not before: with no retired
          key there has only ever been one, and requiring it then would be ceremony.
        */
        var build = () => new AuditChain(
            Options.Create(new AuditOptions
            {
                ChainKey = RotatedKey,
                RetiredChainKeys = [Retired(TestKey, 2)],
                // FoundingChainKey deliberately absent: that absence is what this test is about.
            }),
            NullLogger<AuditChain>.Instance);

        build.Should().Throw<AuditKeyRingException>()
            .WithMessage("*FoundingChainKey is required*");
    }

    [Fact]
    public void AFoundingKeyTheRingDoesNotHold_IsRefused_BecauseItIsADesignationNotACopy()
    {
        // One place per key. A founding key naming material that is neither the current key nor a
        // retired one is a claim with nothing behind it -- and it would verify nothing while looking
        // configured.
        var build = () => new AuditChain(
            Options.Create(new AuditOptions
            {
                ChainKey = RotatedKey,
                RetiredChainKeys = [Retired(TestKey, 2)],
                FoundingChainKey = "a-key-this-deployment-has-never-held-0123456789",
            }),
            NullLogger<AuditChain>.Instance);

        build.Should().Throw<AuditKeyRingException>(
            "⚠️ THIS ASSERTED THE BASE TYPE, AND A DEDUPLICATION MADE THAT MATTER. A sibling test "
            + "asserting AuditKeyRingException was removed as a near-duplicate of this one — the "
            + "names differed by three words — which left this guard covered only by the base class "
            + "that every InvalidOperationException satisfies. Removing a duplicate is right; "
            + "removing the STRONGER of two is how a tidy-up loses coverage")
            .WithMessage("*neither Audit:ChainKey nor one of*");
    }

    [Fact]
    public void BeforeAnyRotation_TheFoundingKeyNeedsNoNaming_BecauseThereIsOnlyOne()
    {
        // The other half of the rule, so the guard cannot quietly become "always required" and turn
        // a fresh deployment into a configuration exercise.
        var build = () => new AuditChain(
            Options.Create(new AuditOptions { ChainKey = TestKey }),
            NullLogger<AuditChain>.Instance);

        build.Should().NotThrow();
    }

    [Fact]
    public async Task ARetiredKeyCannotMintAROWATTHETAIL_BecauseTheRingBoundsItToItsEpoch()
    {
        /*
          THE HAZARD THE TAIL-ANCHOR DECISION NAMED, REPRODUCED BEFORE IT WAS FIXED. Selecting the
          key by KeyId stops a row LYING about which key to check it with — relabelling changes the
          hash. It does not stop a row being MINTED: somebody holding the retired key computes an
          honest hash under it, labels it honestly, and a ring that accepts any member key at any
          sequence verifies it.

          That makes the ring a REGRESSION without a bound. Before it, a retired key verified nothing
          and its holder could forge nothing; after it, the same holder can append. The decision said
          so in advance — "a trial-keyring verifier lets a RETIRED key mint rows at any sequence
          forever, so the forgery surface grows with every rotation, inverting the reason to rotate"
          — and the first version of this ring shipped exactly that, one selection mechanism weaker.

          The bound is the key-epoch boundary the same decision asked for: a retired key is valid
          only up to the sequence at which it was retired. Rows above that are refused even though
          their hash is correct, because a correct hash under a key that had no business writing then
          is precisely what minting looks like.
        */
        await WriteAsync("One", "Two");

        var boundary = await _context.AuditEvents.MaxAsync(e => e.Sequence);

        // The attacker: holds the retired key, writes through it, and does not lie about anything.
        var withRetiredKey = new AuditChain(
            Options.Create(new AuditOptions { ChainKey = TestKey }), NullLogger<AuditChain>.Instance);
        using (var theirs = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(_storeName).Options,
            timeProvider: null,
            auditChain: withRetiredKey))
        {
            theirs.AuditEvents.Add(NewEvent("Minted after the rotation"));
            await theirs.SaveChangesAsync();
        }

        var rotated = new AuditChain(
            Options.Create(new AuditOptions
            {
                ChainKey = RotatedKey,
                RetiredChainKeys = [Retired(TestKey, boundary)],
                FoundingChainKey = TestKey,
            }),
            NullLogger<AuditChain>.Instance);

        var verification = await rotated.VerifyAsync(_context);

        verification.IsIntact.Should().BeFalse(
            "the row hashes correctly under a key that had already been retired when it was written, "
            + "which is minting rather than history");
        verification.FirstBrokenSequence.Should().Be(
            boundary + 1, "the epoch boundary is where a retired key stops being an answer");

        /*
          THE CONTROL, added in review. The boundary is checked BEFORE the hash, so a fixture whose
          minted row did not actually hash correctly would be refused for the right reason by
          accident and everything above would pass while proving nothing. Raising the boundary admits
          the row: that is what makes the refusal a refusal of a genuine forgery.

          Here the row was written through the production write path, so its hash is valid by
          construction — but "by construction" is the kind of reasoning that stops being true after
          an unrelated edit, and the sibling test for the v2 route needs the same control for a
          stronger reason. Asserting it costs one line.
        */
        var admitted = await RotatedRing(lastSequence: boundary + 1).VerifyAsync(_context);
        admitted.IsIntact.Should().BeTrue(
            "with the boundary raised past it the minted row verifies, which is what proves its hash "
            + "was genuinely valid under the retired key rather than merely wrong");

        /*
          AND THE VERDICT MUST SAY WHICH FAILURE THIS IS. An expired boundary and an unknown key id
          both leave the walk with no key, and they used to produce the same sentence -- "no key in
          this ring has that id", which is FALSE here because the ring does hold it. The remedies are
          opposite, and the wrong one is available: raising LastSequence turns this verdict green,
          and if the row really was minted after the retirement, raising it completes the attack.
          Raised in review on 32770df.
        */
        verification.Reason.Should().Contain(
            "which this verification DOES hold",
            "the ring has the key — saying otherwise sends the operator to add what is already there");
        verification.Reason.Should().Contain(
            "retired at sequence", "the boundary is the fact that decides this");
        verification.Reason.Should().NotContain(
            "no key in this verification's ring",
            "that sentence belongs to the OTHER failure and would prescribe the wrong fix");
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
    public async Task AWriteOnARowTheRINGAnsweredFor_DoesNotBlameTheCURRENTKey()
    {
        /*
          A VERDICT THAT NAMES THE WRONG KNOB IS A WRONG VERDICT, even when its conclusion is right.
          The exoneration arm was written before the ring existed and said the row "records the key
          identity that the configured Audit:ChainKey derives". After a rotation that is FALSE by
          construction: the row names the RETIRED key, the ring selected it by that id, and
          Audit:ChainKey is a key this row has nothing to do with. An operator checking the sentence
          against the configuration finds two ids that do not match and re-opens a key question the
          tool had already closed -- while an actual write goes unescalated.

          The conclusion the sentence carries is the one that matters and it is unchanged: the key is
          not in question, this is a WRITE. Only the reason given for it was stale.

          FALSIFIED by restoring the old wording: both assertions below redden — the NotContain on
          "the configured Audit:ChainKey", and the Contain on "the verification ring SELECTED that
          key".

          (This named a Contain("ring") assertion. There is no such assertion: the commit that
          replaced it with the sentence above left this note pointing at the line it deleted. It was
          also the weaker of the two, since "ring" is a substring of "during" and "string".)

          Raised in review on 9e92377.
        */
        await WriteAsync("Written under the key that was later retired");
        var boundary = await _context.AuditEvents.MaxAsync(e => e.Sequence);

        var row = await _context.AuditEvents.FirstAsync();
        row.Detail = "altered after it was written";   // KeyId and PayloadVersion left alone
        await _context.SaveChangesAsync();

        var rotated = new AuditChain(
            Options.Create(new AuditOptions
            {
                ChainKey = RotatedKey,
                RetiredChainKeys = [Retired(TestKey, boundary)],
                FoundingChainKey = TestKey,
            }),
            NullLogger<AuditChain>.Instance);

        var verification = await rotated.VerifyAsync(_context);

        verification.Kind.Should().Be(AuditChainBreakKind.HashMismatch);
        verification.RecordedKeyId.Should().Be(
            AuditChain.DeriveKeyId(TestKey), "the row names the key that wrote it");
        verification.ConfiguredKeyId.Should().NotBe(
            verification.RecordedKeyId,
            "THE STATE THAT MADE THE OLD SENTENCE FALSE -- the row was checked under a key the "
            + "current one does not derive, which before the ring could not happen");

        verification.Reason.Should().NotContain(
            "the configured Audit:ChainKey",
            "Audit:ChainKey did not derive this row's id and pointing an operator at it re-opens a "
            + "key question the ring had already answered");
        verification.Reason.Should().Contain(
            "the verification ring SELECTED that key",
            "the four-letter token this used to assert matches \"during\", \"string\" and any "
            + "future wording that happens to contain them — the claim is that SELECTION happened, "
            + "so the claim is what to assert");

        var (exitCode, lines) = VerifyCommand.Report(verification, 1, boundary);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(VerifyCommand.Broken);
        text.Should().Contain("WRITE", "the conclusion is unchanged: escalate, do not re-check keys");
        text.Should().NotContain(
            "configured Audit:ChainKey derives",
            "THE SAME STALE SENTENCE LIVED IN THE TOOL TOO, which is the copy an operator actually "
            + "reads during an incident -- the review named the library and this is the class");
    }

    [Fact]
    public async Task AWriteOnAnIDENTITYLESSRow_PointsAtTheFOUNDINGKey_NotTheCurrentOne()
    {
        /*
          THE OTHER HALF OF THE SAME DEFECT, one `if` away and not raised in review. A LEGACY row
          recording no key identity is checked under Audit:FoundingChainKey -- that is the whole
          reason the founding key has to be named rather than assumed. (The version matters: a
          CURRENT-version row recording none is refused as UnknownScheme instead, since its version
          does keep the value.) The ambiguity arm still told the operator
          the alternative was "a different Audit:ChainKey from the one it was written with", which
          after a rotation sends them to a key that never touched the row.

          Fixing the arm the review pointed at and leaving this one is the repeating defect in this
          project: the instance gets fixed, the class does not.

          FALSIFIED by restoring "using a different Audit:ChainKey": the Contain assertion reddens.
        */
        var row = NewEvent("WrittenBeforeKeyIdentityExisted");
        _context.AuditEvents.Add(row);
        await _context.SaveChangesAsync();

        // Demote to the legacy scheme and break the hash in the same edit: Pending() filters
        // EntityState.Added, so a Modified row is never re-hashed on the way out.
        row.PayloadVersion = "v2";
        row.KeyId = null;
        row.RowHash = new string('0', 64);
        await _context.SaveChangesAsync();

        var verification = await _chain.VerifyAsync(_context);

        verification.Kind.Should().Be(AuditChainBreakKind.HashMismatch);
        verification.RecordedKeyId.Should().BeNull("this is the identity-less arm");
        verification.Reason.Should().Contain(
            "Audit:FoundingChainKey",
            "that is the key the row was actually checked under, and it is the one an operator has "
            + "to compare against the rotation record");

        /*
          ⚠️ THE 'NotTheCurrentOne' HALF OF THIS TEST'S NAME WAS CARRIED BY THE TITLE ALONE. Three
          assertions, all of them about what the verdict DOES say, and none about the wording the
          test exists to keep out -- the old alternative, which sent an operator to the current key
          for a row the founding key answers for. A blanket NotContain("Audit:ChainKey") is not
          available: the corrected text names it legitimately, in "Audit:FoundingChainKey, which is
          Audit:ChainKey only while nothing has been retired". What must be absent is the OLD
          ALTERNATIVE, so that is what is asserted.
        */
        verification.Reason.Should().NotContain(
            "a different Audit:ChainKey from the one it was written with",
            "the alternative this arm used to offer names a key that never touched the row");
    }

    [Fact]
    public async Task AV2RowMintedWithTheRETIREDFoundingKey_IsRefused_BecauseTheEpochBoundsItToo()
    {
        /*
          THE BOUNDARY WAS OPTIONAL, AND THE FORGER PICKED THE PAYLOAD VERSION. Bounding retired keys
          by KeyId bounds every key a row can NAME. A 'v2' row names none — that version records no
          key identity — so it is checked under Audit:FoundingChainKey without naming it, and the
          KeyId-keyed bound never ran. Labelling a minted row 'v2' therefore reached the retired
          founding key and skipped the epoch check entirely.

          MEASURED BEFORE THE FIX: such a row, at the tail, above the founding key's boundary,
          verified clean — IsIntact = True, Verified = 3. Before the ring the same row needed the
          CURRENT key, so this was the ring handing an old key a power it did not have. That is the
          exact regression RetiredChainKey's own documentation says the boundary exists to prevent,
          reached by choosing a different payload version.

          THE FIRST ASSERTION IS A CONTROL AND IT IS NOT OPTIONAL. The boundary is checked BEFORE
          the hash, so a fixture whose forged hash is simply WRONG would be refused for the right
          reason by accident and this test would pass while proving nothing. Verifying first with a
          boundary high enough to admit the row proves the hash is genuinely valid under the founding
          key — that this is a real forgery rather than a broken fixture — and only then does the
          refusal below mean anything.

          Raised in review on 7370276 as Major, and it was right.
        */
        await WriteAsync("Before", "Rotation");
        var boundary = await _context.AuditEvents.MaxAsync(e => e.Sequence);

        _context.AuditEvents.Add(NewEvent("MintedWithTheRetiredFoundingKey"));
        await _context.SaveChangesAsync();

        // Demote it to the legacy scheme and hash it as one. Pending() filters EntityState.Added, so
        // a Modified row is never re-hashed on the way out — which is how a raw-SQL insert looks.
        var minted = await _context.AuditEvents.OrderByDescending(e => e.Sequence).FirstAsync();
        minted.PayloadVersion = "v2";
        minted.KeyId = null;
        minted.RowHash = LegacyHash(TestKey, minted);
        await _context.SaveChangesAsync();

        minted.Sequence.Should().Be(boundary + 1, "the forgery has to sit ABOVE the boundary");

        AuditChain Ring(long lastSequence) => new(
            Options.Create(new AuditOptions
            {
                ChainKey = RotatedKey,
                RetiredChainKeys = [Retired(TestKey, lastSequence)],
                FoundingChainKey = TestKey,
            }),
            NullLogger<AuditChain>.Instance);

        /*
          ⚠️ THE CONTROL SITS EXACTLY ON THE BOUNDARY, AND IT USED TO SIT TEN ABOVE IT. Ten of slack
          proves the hash is valid, which is what a control is for, but it leaves the comparison
          itself untested: no assertion in this file ever put a 'v2' row at exactly
          _foundingLastSequence, so flipping `row.Sequence > foundingLast` to `>=` changed nothing
          any test could see. The epoch's upper end is INCLUSIVE -- a key answers for the row it
          stopped at -- and the control is the only place that says so.
        */
        var admitted = await Ring(minted.Sequence).VerifyAsync(_context);
        admitted.IsIntact.Should().BeTrue(
            "THE CONTROL, and the inclusive end of the epoch: with the boundary recorded AT this "
            + "row the key still answers for it, which proves the hash really is valid under the "
            + "retired founding key — so the refusal below is a refusal of a genuine forgery and "
            + "not of a broken fixture, and `>` is not `>=`");

        var refused = await Ring(boundary).VerifyAsync(_context);

        refused.IsIntact.Should().BeFalse(
            "the row hashes correctly under a key retired before it was written, which is minting");
        refused.Kind.Should().Be(AuditChainBreakKind.UnknownScheme);
        refused.FirstBrokenSequence.Should().Be(boundary + 1);
        refused.Reason.Should().Contain(
            "Audit:FoundingChainKey",
            "the operator has to be told WHICH key answered for this row, and it is not the one the "
            + "row names — it names none");
        refused.Reason.Should().Contain(
            "MINTING",
            "on this shape minting is the LEADING reading, not the alternative: a row above the "
            + "founding boundary carrying no identity is the one way to reach that key unnamed");
    }

    /// <summary>
    /// An INDEPENDENT rendering of the legacy payload, so the fixture above does not simply agree
    /// with whatever the production hasher does. It is the same technique the frozen literal in
    /// <c>ALegacyRowWithNoKeyIdentity_StillVerifies_UnderTheFoundingKey</c> uses, computed here
    /// because this row's identifiers are generated rather than fixed.
    /// </summary>
    private static string LegacyHash(string key, AuditEvent row)
    {
        var payload = string.Join('|',
            row.PayloadVersion,
            row.Id.ToString("N"),
            row.Sequence.ToString(CultureInfo.InvariantCulture),
            row.OccurredAt.Ticks.ToString(CultureInfo.InvariantCulture),
            row.Event,
            row.Outcome.ToString(),
            row.ActorUserId?.ToString("N") ?? string.Empty,
            row.SubjectType ?? string.Empty,
            row.SubjectId?.ToString("N") ?? string.Empty,
            row.TraceId ?? string.Empty,
            row.PreviousHash ?? string.Empty,
            row.Detail ?? string.Empty);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    [Fact]
    public async Task TheNEWESTRetiredKeyCannotREAUTHORWhatOlderKeysWrote_BecauseAnEpochHasTwoEnds()
    {
        /*
          AN EPOCH HAS TWO ENDS. THE FIRST VERSION OF THIS RING GAVE IT NONE, THE SECOND GAVE IT
          ONE. Every boundary check
          was `row.Sequence > last`. An upper bound stops a retired key minting ABOVE its retirement;
          nothing stopped it answering for every sequence BELOW, including the stretches that older
          keys wrote.

          MEASURED BEFORE THE FIX, and this test is that measurement: with TestKey retired at 2 and
          SecondKey retired at 4, the holder of SecondKey re-authored sequences 1 through 4 --
          relabelling the first two rows, which TestKey wrote -- and the walk returned
          IsIntact = True with four rows verified.

          So compromising the NEWEST retired key handed over the whole history rather than one
          epoch, and every further rotation made the prize bigger instead of smaller. That is the
          second time on this branch that a half-bounded ring inverted the reason to rotate.

          The lower bound is DERIVED, never configured: the recorded boundaries already partition the
          sequence space, so a key that stopped at N was preceded by one that stopped at N' and its
          epoch is (N', N]. Asking for the start as well would be a second place to state one fact.
        */
        var second = "unit-test-audit-chain-key-the-SECOND-retired-0123456789";

        await WriteAsync("TestKey one", "TestKey two");

        // Sequences 3 and 4, written under the key that is retired second.
        using (var epochTwo = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(_storeName).Options,
            timeProvider: null,
            auditChain: new AuditChain(
                Options.Create(new AuditOptions { ChainKey = second }),
                NullLogger<AuditChain>.Instance)))
        {
            epochTwo.AuditEvents.Add(NewEvent("SecondKey three"));
            await epochTwo.SaveChangesAsync();
            epochTwo.AuditEvents.Add(NewEvent("SecondKey four"));
            await epochTwo.SaveChangesAsync();
        }

        AuditChain Ring() => new(
            Options.Create(new AuditOptions
            {
                ChainKey = RotatedKey,
                RetiredChainKeys = [Retired(TestKey, 2), Retired(second, 4)],
                FoundingChainKey = TestKey,
            }),
            NullLogger<AuditChain>.Instance);

        var honest = await Ring().VerifyAsync(_context);
        honest.IsIntact.Should().BeTrue(
            "THE CONTROL: two rotations, nothing touched — an honest table with three epochs still "
            + "verifies, or the bound below would be refusing history rather than forgery");
        honest.Verified.Should().Be(4);

        // The attacker holds the SECOND retired key and re-authors the whole prefix under it,
        // including the two rows the FIRST key wrote. Nothing is minted above any boundary.
        using (var theirs = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(_storeName).Options,
            timeProvider: null,
            auditChain: new AuditChain(
                Options.Create(new AuditOptions { ChainKey = second }),
                NullLogger<AuditChain>.Instance)))
        {
            string? previous = null;
            foreach (var row in await theirs.AuditEvents.OrderBy(e => e.Sequence).ToListAsync())
            {
                row.Detail = $"re-authored at {row.Sequence}";
                row.KeyId = AuditChain.DeriveKeyId(second);
                row.PreviousHash = previous;
                row.RowHash = CurrentHash(second, row);
                previous = row.RowHash;
            }

            await theirs.SaveChangesAsync();
        }

        _context.ChangeTracker.Clear();
        var forged = await Ring().VerifyAsync(_context);

        forged.IsIntact.Should().BeFalse(
            "the two oldest rows name a key whose epoch begins at 3, so it had not started writing "
            + "when they were written");
        forged.FirstBrokenSequence.Should().Be(
            1, "and it breaks at the FIRST re-authored row, not somewhere in the middle");
        forged.Reason.Should().Contain("epoch begins at 3");

        /*
          ⚠️ AND THE ROW AT EXACTLY FirstSequence - 1, WHICH NOTHING ELSE PINS. Re-authoring all four
          rows puts the break at 1, two below the epoch start of 3 -- so `row.Sequence < FirstSequence`
          and `row.Sequence < FirstSequence - 1` produce byte-identical results and the off-by-one is
          invisible. The interesting row is 2: the last row the FIRST key wrote, one below the second
          key's epoch. Restoring row 1 to its honest author leaves exactly that shape.

          It matters because the loose form is the permissive one: it would let the holder of the
          NEWEST retired key re-author the boundary row of the key beneath it, one row per rotation.
        */
        using (var restore = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(_storeName).Options,
            timeProvider: null,
            auditChain: Ring()))
        {
            var first = await restore.AuditEvents.OrderBy(e => e.Sequence).FirstAsync();
            first.Detail = "TestKey one";
            first.KeyId = AuditChain.DeriveKeyId(TestKey);
            first.PreviousHash = null;
            first.RowHash = CurrentHash(TestKey, first);

            var boundaryRow = await restore.AuditEvents.SingleAsync(e => e.Sequence == 2);
            boundaryRow.PreviousHash = first.RowHash;
            boundaryRow.RowHash = CurrentHash(second, boundaryRow);

            await restore.SaveChangesAsync();
        }

        _context.ChangeTracker.Clear();
        var onTheBoundary = await Ring().VerifyAsync(_context);

        onTheBoundary.FirstBrokenSequence.Should().Be(
            2,
            "the row at exactly FirstSequence - 1 has to be refused, or the newest retired key "
            + "reaches one row into the epoch below it");
        onTheBoundary.Kind.Should().Be(AuditChainBreakKind.UnknownScheme);
        onTheBoundary.Verified.Should().Be(1, "row 1 is honest again and verifies under its own key");
    }

    /// <summary>
    /// An INDEPENDENT rendering of the current payload, for the same reason
    /// <see cref="LegacyHash"/> exists: a fixture that asks the production hasher for its forgery
    /// agrees with whatever that hasher does, including a broken arm.
    /// </summary>
    private static string CurrentHash(string key, AuditEvent row)
    {
        var payload = string.Join('|',
            row.PayloadVersion,
            row.KeyId ?? string.Empty,
            row.Id.ToString("N"),
            row.Sequence.ToString(CultureInfo.InvariantCulture),
            row.OccurredAt.Ticks.ToString(CultureInfo.InvariantCulture),
            row.Event,
            row.Outcome.ToString(),
            row.ActorUserId?.ToString("N") ?? string.Empty,
            row.SubjectType ?? string.Empty,
            row.SubjectId?.ToString("N") ?? string.Empty,
            row.TraceId ?? string.Empty,
            row.PreviousHash ?? string.Empty,
            row.Detail ?? string.Empty);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    [Fact]
    public async Task AnIDENTITYLESSRowBELOWTheFoundingEpoch_IsSentToTheDESIGNATION_NotToABoundary()
    {
        /*
          THE ARM THAT DID NOT EXIST, AND THE VERDICT IT USED TO BORROW WAS WRONG TWICE OVER.

          Until the arms were split, a row reaching the below-the-epoch refusal was told it "names a
          key whose epoch begins at N". A 'v2' row names nothing — the tool printed "key id '(none)'"
          one line under that same sentence. Worse than the false description: BOTH remedies it
          offered were the wrong ones. It sent the operator to a recorded LastSequence, and
          _foundingFirstSequence is INHERITED from the entry the designation names, so no boundary
          edit moves it. The prescribed first action produces no change at all while the actual
          misconfiguration stays exactly where it is.

          There is exactly one way to reach this, and it is not an attack: Audit:FoundingChainKey
          designates a ring member that is not the OLDEST, so the epoch it opens starts above the
          identity-less rows — which are the oldest rows there are.
        */
        var second = "a-second-retired-key-0123456789abcdefghij";

        await WriteAsync("One", "Two");

        using (var epochTwo = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(_storeName).Options,
            timeProvider: null,
            auditChain: new AuditChain(
                Options.Create(new AuditOptions { ChainKey = second }),
                NullLogger<AuditChain>.Instance)))
        {
            epochTwo.AuditEvents.Add(NewEvent("Three"));
            await epochTwo.SaveChangesAsync();
        }

        // Sequence 1 demoted to the legacy scheme, hashed honestly under the SECOND key so that the
        // refusal below is a refusal of the epoch and not an accident of a broken fixture.
        var oldest = await _context.AuditEvents.OrderBy(e => e.Sequence).FirstAsync();
        oldest.PayloadVersion = "v2";
        oldest.KeyId = null;
        oldest.RowHash = LegacyHash(second, oldest);
        await _context.SaveChangesAsync();

        AuditChain Ring(string founding) => new(
            Options.Create(new AuditOptions
            {
                ChainKey = RotatedKey,
                RetiredChainKeys = [Retired(TestKey, 2), Retired(second, 3)],
                FoundingChainKey = founding,
            }),
            NullLogger<AuditChain>.Instance);

        _context.ChangeTracker.Clear();
        var refused = await Ring(second).VerifyAsync(_context);

        refused.Kind.Should().Be(AuditChainBreakKind.UnknownScheme);
        refused.FirstBrokenSequence.Should().Be(1);
        refused.RecordedKeyId.Should().BeNull("a 'v2' row records no key identity");

        refused.Reason.Should().NotContain(
            "names a key",
            "THE SENTENCE THIS ARM EXISTS TO STOP: the row carries no key id, and the tool prints "
            + "\"key id '(none)'\" one line below this reason");
        refused.Reason.Should().Contain(
            "The fix is Audit:FoundingChainKey",
            "the designation is the ONLY thing that can produce this, so it is the only thing worth "
            + "naming");
        refused.Reason.Should().Contain(
            "point it at the OLDEST key in the ring",
            "the designation is the fix, and the verdict has to say which way to point it");
        refused.Reason.Should().Contain(
            "a second finding rather than a fix",
            "⚠️ THIS REASON HAS BEEN WRONG THREE TIMES, AND TWICE IT SAID \"MEASURED\". It first "
            + "pinned a false absolute — \"No LastSequence edit can move this\" — when the epoch's "
            + "start IS derived from the preceding entry's boundary. The first repair claimed the "
            + "edit makes the row stop being refused and the walk fail on the LINK instead, and "
            + "called that measured; no configuration value can produce a link break, since that "
            + "test compares two STORED columns before any key is selected. The second said the "
            + "start \"floors at 2\" and the row \"is older than that\", true HERE and not in "
            + "general: the floor is the designation's POSITION in boundary order, and nothing in "
            + "the code puts the row at sequence 1 — only the shape of an honest table does. In "
            + "this fixture the ring is [Retired(TestKey, 2), Retired(second, 3)] designating "
            + "'second', the founding epoch is [3,3], the lowest legal preceding boundary is 1, so "
            + "the start becomes 2 and the row at sequence 1 stays refused. The verdict has to say "
            + "the edit moves the start, still refuses this row, and that clearing it would mean "
            + "the trail does not begin at 1");
    }

    [Fact]
    public async Task ACurrentVersionRowWithNOKeyIdentity_GetsItsOwnVerdict_NotTheUnknownIdOne()
    {
        /*
          The mirror of the 'v2' row carrying an id, and it had no arm of its own: it fell to the
          default, which told the operator the row "was written under key id '(none)' and no key in
          this verification's ring has that id". No key has an id that is not an id. The situation is
          not an unknown key at all — it is a column that should carry an identity and does not, on a
          version that has somewhere to keep one.
        */
        var row = NewEvent("CurrentVersionStrippedOfItsIdentity");
        _context.AuditEvents.Add(row);
        await _context.SaveChangesAsync();

        row.KeyId = null;
        await _context.SaveChangesAsync();

        var verification = await _chain.VerifyAsync(_context);

        verification.Kind.Should().Be(AuditChainBreakKind.UnknownScheme);
        verification.RecordedKeyId.Should().BeNull();
        verification.Reason.Should().NotContain(
            "no key in this verification's ring has that id",
            "there is no id for the ring to fail to hold — that sentence belongs to a row that names "
            + "something the ring does not have");
        verification.Reason.Should().Contain(
            "records the identity of the key that wrote it, and records none",
            "the verdict has to say what is actually wrong: a column that this version keeps and "
            + "this row does not carry");
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
