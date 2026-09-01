using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// The anchor record: what it commits to, what it refuses, and — twice, on purpose — what it does
/// not detect.
/// </summary>
public class AuditAnchorChainTests : IDisposable
{
    private const string ChainKey = "unit-test-audit-chain-key-0123456789abcdef";
    private const string AnchorKey = "unit-test-anchor-key-quite-unlike-the-other-one";
    private const string RotatedChainKey = "unit-test-chain-key-AFTER-the-rotation-9876543210";

    private readonly AzureBankDbContext _context;
    private readonly AuditChain _chain;
    private readonly AuditAnchorChain _anchors;

    public AuditAnchorChainTests()
    {
        var options = Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey });
        _chain = new AuditChain(options, NullLogger<AuditChain>.Instance);
        _anchors = new AuditAnchorChain(options);
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

    private async Task WriteRowsAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _context.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTime.UtcNow,
                Event = $"Event{i}",
                Outcome = AuditOutcome.Succeeded,
                ActorUserId = Guid.NewGuid(),
                RowHash = string.Empty,
            });
            await _context.SaveChangesAsync();
        }
    }

    private async Task<AuditAnchor> AnchorAsync()
    {
        var tail = await _anchors.ReadTailAsync(_context);
        var record = _anchors.Build(await _chain.VerifyAsync(_context), tail, DateTime.UtcNow);
        _context.Set<AuditAnchor>().Add(record);
        await _context.SaveChangesAsync();
        return record;
    }

    [Fact]
    public void TheAnchorKeyIdIsDerivedFromTheKey_AndHasAKnownAnswer()
    {
        /*
          THE ONLY GUARD ON THE DERIVATION, and it needs one for the same reason its sibling does:
          the algorithm, the domain string and the truncation length are all frozen the moment the
          first record hashes one. Measured by RUNNING this, never recomputed here.
        */
        AuditAnchorChain.DeriveAnchorKeyId(AnchorKey).Should().Be(
            "d7336095ace36a45",
            "the domain string, the algorithm and the truncation length produce exactly this");

        AuditAnchorChain.DeriveAnchorKeyId(AnchorKey).Should().NotBe(
            AuditChain.DeriveKeyId(AnchorKey),
            "the anchor and the row identity schemes use SEPARATE domain strings, so the same key "
            + "must not produce the same identity in both -- otherwise the two constants are welded "
            + "and bumping either silently re-derives the other");
    }

    [Fact]
    public async Task AnAnchorOverAnIntactChain_CommitsToTheCountAndTheTail()
    {
        await WriteRowsAsync(3);
        var record = await AnchorAsync();

        record.Kind.Should().Be(AuditAnchorKind.Anchor);
        record.AnchorSequence.Should().Be(1);
        record.CoveredRowCount.Should().Be(3);
        record.CoveredThroughSequence.Should().Be(3);
        record.LowestCoveredSequence.Should().Be(1);
        record.PreviousAnchorPayloadHash.Should().BeNull("nothing precedes the first record");
        record.AnchoredValue.Should().NotBeNull();
        record.VerifiedUnderChainKeyId.Should().Be(
            AuditChain.DeriveKeyId(ChainKey),
            "⚠️ AND THIS PASSES UNDER BOTH WRITERS, so it is not the test that pins the field. This "
            + "fixture holds ONE key, so the key the run holds and the key that wrote the tail are "
            + "the same string, and the version of this line that named the CURRENT key "
            + "unconditionally was green here for as long as it existed. It is a correct statement "
            + "about a deployment that never rotated and nothing more. The test that can tell them "
            + "apart is AnAnchorTakenAfterARotation_NamesTheRETIREDKeyThatAuthenticatedTheTail");

        var tail = await _context.AuditEvents.OrderByDescending(e => e.Sequence).FirstAsync();
        record.TailRowHash.Should().Be(
            tail.RowHash, "the anchored tail must be the tail the walk actually verified");

        _anchors.Check(record).Should().Be(AuditAnchorCheck.Authentic);
    }

    [Fact]
    public async Task AnchorsChainToEachOther()
    {
        await WriteRowsAsync(2);
        var first = await AnchorAsync();
        await WriteRowsAsync(1);
        var second = await AnchorAsync();

        second.AnchorSequence.Should().Be(2);
        second.PreviousAnchorPayloadHash.Should().Be(
            first.PayloadHash, "or deleting a record from the middle would leave no trace");
        second.CoveredThroughSequence.Should().Be(3);
        _anchors.Check(second).Should().Be(AuditAnchorCheck.Authentic);
    }

    [Fact]
    public async Task TwoAnchorsOverAnUNCHANGEDChain_StillCommitToDifferentValues()
    {
        /*
          WITHOUT THIS, A TIMESTAMP TOKEN IS RELOCATABLE BETWEEN THEM. A timestamp authority is
          required not to examine what it signs, so two anchors sharing an imprint could have each
          other's tokens moved across with nothing downstream objecting. The record's own counter and
          its link are inside the anchored value precisely to stop that.

          FALSIFIED by removing BOTH AnchorSequence AND PreviousAnchorPayloadHash from the anchored
          value -- measured, either one alone still distinguishes the two, which is the redundancy
          being bought rather than an accident. Saying "or" here would have been a falsification note
          that does not falsify, and the note is the only thing standing between this test and
          somebody trusting it for the wrong reason.
        */
        await WriteRowsAsync(2);
        var first = await AnchorAsync();
        var second = await AnchorAsync();

        second.CoveredRowCount.Should().Be(first.CoveredRowCount, "the chain did not change");
        second.TailRowHash.Should().Be(first.TailRowHash, "and neither did its tail");
        second.AnchoredValue.Should().NotBe(
            first.AnchoredValue, "yet the value a third party would attest must still differ");
    }

    [Fact]
    public async Task AlteringAnyFieldOfARecord_BreaksItsAuthenticationCode()
    {
        await WriteRowsAsync(2);
        var record = await AnchorAsync();

        record.CoveredRowCount = 1;
        _anchors.Check(record).Should().Be(
            AuditAnchorCheck.MacMismatch,
            "the count is the only anchored quantity that constrains the interior, so it is the "
            + "first thing worth lying about");
    }

    [Fact]
    public async Task FlippingAnAnchorIntoAGapMarker_BreaksItsAuthenticationCode()
    {
        /*
          THE CHEAPEST ATTACK THE MAC ACTUALLY STOPS. Flipping Kind costs one UPDATE and no key, and
          it would collapse the operator's provable bound back to the previous record while every
          other check still passed. It is why Kind is inside the payload for BOTH kinds rather than
          omitted when it is the common one.

          FALSIFIED by removing Kind from the rendered payload.
        */
        await WriteRowsAsync(2);
        var record = await AnchorAsync();

        record.Kind = AuditAnchorKind.GapMarker;
        _anchors.Check(record).Should().Be(AuditAnchorCheck.MacMismatch);
    }

    [Fact]
    public async Task RenumberingARecord_BreaksItsAuthenticationCode()
    {
        await WriteRowsAsync(2);
        var record = await AnchorAsync();

        record.AnchorSequence = 7;
        _anchors.Check(record).Should().Be(
            AuditAnchorCheck.MacMismatch,
            "otherwise an old record covering very little is renumbered into the newest slot with "
            + "its code still valid");
    }

    [Fact]
    public async Task ARecordNamingAKeyThisRunDoesNotHold_IsUNCHECKED_NotWrong()
    {
        /*
          THE ANTI-MUZZLE, one layer up from the row chain's. If "I cannot check this" and "this is
          wrong" were the same answer, overwriting a tampered record's key identity would soften the
          verdict from tampering to housekeeping. They are separate values, and the caller refuses to
          build on either.

          FALSIFIED by folding UnknownScheme into MacMismatch.
        */
        await WriteRowsAsync(2);
        var record = await AnchorAsync();

        record.AnchorKeyId = "ffffffffffffffff";
        _anchors.Check(record).Should().Be(AuditAnchorCheck.UnknownScheme);

        record.AnchorKeyId = AuditAnchorChain.DeriveAnchorKeyId(AnchorKey);
        record.PayloadVersion = "a9";
        _anchors.Check(record).Should().Be(AuditAnchorCheck.UnknownScheme);
    }

    [Fact]
    public async Task AnAnchorTakenAfterARotation_NamesTheRETIREDKeyThatAuthenticatedTheTail()
    {
        /*
          THE WINDOW A ROTATION OPENS, WHICH IS THE ONLY PLACE THIS FIELD WAS EVER WRONG. Between
          rotating Audit:ChainKey and writing the first row under the new key, a walk certifies a
          tail that a RETIRED key authenticated. The old writer filed that tail under the CURRENT
          key's identity -- an anchor naming one key beside a hash only another key can check, in
          the record whose AnchoredValue is meant to be handed to somebody outside this system.

          ADR-0044 D7 recorded it as deferred rather than forgotten. This is the test that closes it.

          The anchor key must be IDENTICAL across both rings or Check refuses at the AnchorKeyId
          gate before it ever renders the payload, and the assertion below would pass for the wrong
          reason. Only the CHAIN key rotates here.
        */
        await WriteRowsAsync(3);

        var rotated = Options.Create(new AuditOptions
        {
            ChainKey = RotatedChainKey,
            AnchorKey = AnchorKey,
            RetiredChainKeys = [new RetiredChainKey { Key = ChainKey, LastSequence = 3 }],
            FoundingChainKey = ChainKey,
        });

        var chainAfter = new AuditChain(rotated, NullLogger<AuditChain>.Instance);
        var anchorsAfter = new AuditAnchorChain(rotated);

        var record = anchorsAfter.Build(
            await chainAfter.VerifyAsync(_context), tail: null, DateTime.UtcNow);

        record.Kind.Should().Be(AuditAnchorKind.Anchor, "the rows are untouched");
        record.VerifiedUnderChainKeyId.Should().Be(
            AuditChain.DeriveKeyId(ChainKey),
            "ChainKey wrote all three rows and is now retired, so it is the key that authenticated "
            + "the tail hash this record publishes");
        record.VerifiedUnderChainKeyId.Should().NotBe(
            AuditChain.DeriveKeyId(RotatedChainKey),
            "the current key has never written a row here and cannot check the tail. Naming it was "
            + "the defect: right on every deployment that never rotated, wrong in this window");
        anchorsAfter.Check(record).Should().Be(
            AuditAnchorCheck.Authentic,
            "the new value has to round-trip through the payload and the MAC, not merely be "
            + "assigned -- element nine travels inside AnchoredValue");
    }

    [Fact]
    public async Task AGapMarkerNamesTheKeyTheRUNHeld_BecauseThereIsNoTailToName()
    {
        /*
          THE FALLBACK, AND IT IS NOT A LEFTOVER OF THE OLD BEHAVIOUR. Where the walk certifies no
          tail there is no key that authenticated one, and the run's own key is the only honest
          answer -- a run always holds one. So the field's meaning depends on Kind, deliberately:
          Anchor means "the key behind TailRowHash", GapMarker means "the key this run held".

          That asymmetry is legible because Kind and five NULL coverage columns already say there is
          no tail. The alternatives are worse: NULL needs a migration off IsRequired, and
          string.Empty in an nchar(16) reads back as sixteen spaces and breaks the MAC.
        */
        var record = _anchors.Build(
            await _chain.VerifyAsync(_context), tail: null, DateTime.UtcNow);

        record.Kind.Should().Be(AuditAnchorKind.GapMarker, "the table is empty");
        record.TailRowHash.Should().BeNull("there is no tail");
        record.VerifiedUnderChainKeyId.Should().Be(
            AuditChain.DeriveKeyId(ChainKey),
            "with no tail to name, the key the run held is what the field can honestly say");
        _anchors.Check(record).Should().Be(AuditAnchorCheck.Authentic);
    }

    [Fact]
    public async Task OverwritingTheChainKeyIdentity_BreaksItsAuthenticationCode()
    {
        /*
          ELEMENT NINE IS IN THE PAYLOAD, AND NOTHING PROVED IT WAS. Measured while this change was
          designed: deleting VerifiedUnderChainKeyId from BOTH RenderPayload and ComputeAnchoredValue
          left the whole audit suite green -- 112 of 112 -- with Kind used as a positive control that
          did redden. AlteringAnyFieldOfARecord_BreaksItsAuthenticationCode mutates one field only,
          so it does not cover this one despite its name.

          That matters more now than it did yesterday: this change is the reason to trust the field,
          and a field outside the MAC is one anybody with the database can rewrite.
        */
        await WriteRowsAsync(2);
        var record = await AnchorAsync();

        record.VerifiedUnderChainKeyId = "ffffffffffffffff";

        _anchors.Check(record).Should().Be(
            AuditAnchorCheck.MacMismatch,
            "the key identity is element nine of the payload, so rewriting it has to invalidate the "
            + "code -- otherwise the record says which key covered the tail and nothing defends it");
    }

    [Fact]
    public async Task ARecordMACedUnderADifferentKey_IsUNCHECKED_NotWrong()
    {
        await WriteRowsAsync(2);
        var record = await AnchorAsync();

        var other = new AuditAnchorChain(Options.Create(
            new AuditOptions { ChainKey = ChainKey, AnchorKey = "a-completely-different-anchor-key-0123456789" }));

        other.Check(record).Should().Be(
            AuditAnchorCheck.UnknownScheme,
            "the record names a key this run does not hold, which is not the same as being wrong");
    }

    [Fact]
    public async Task ABrokenChainProducesAGapMarker_ThatCoversNothing()
    {
        await WriteRowsAsync(2);
        var row = await _context.AuditEvents.OrderBy(e => e.Sequence).FirstAsync();
        row.Detail = "altered";
        await _context.SaveChangesAsync();

        var record = await AnchorAsync();

        record.Kind.Should().Be(AuditAnchorKind.GapMarker);
        record.CoveredRowCount.Should().BeNull();
        record.CoveredThroughSequence.Should().BeNull();
        record.TailRowHash.Should().BeNull();
        record.AnchoredValue.Should().BeNull(
            "a marker asserts coverage of nothing, so flipping it into an anchor cannot mint a "
            + "claim it never carried");
        _anchors.Check(record).Should().Be(
            AuditAnchorCheck.Authentic, "a marker is authenticated exactly like an anchor");
    }

    [Fact]
    public async Task AnEmptyAuditTableProducesAGapMarker_BecauseEmptyIsNotTheSameAsUnused()
    {
        var record = await AnchorAsync();

        record.Kind.Should().Be(AuditAnchorKind.GapMarker);
        record.AnchorSequence.Should().Be(
            1, "recording that the table was empty on this date is itself evidence -- a table "
            + "truncated to nothing reports exactly what a fresh one does");
    }

    [Fact]
    public async Task AnchorRecordsAreInsertOnly()
    {
        await WriteRowsAsync(2);
        var record = await AnchorAsync();

        record.CoveredRowCount = 99;
        var act = async () => await _context.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*insert-only*");
    }

    [Fact]
    public async Task RewritingAStoredPayloadHash_IsCaught_EvenThoughTheCodeStillVerifies()
    {
        /*
          THE ONE DERIVED VALUE THE AUTHENTICATION CODE CANNOT COVER. PayloadHash is a hash OF the
          payload, so it cannot be an element of it -- which means the code verifies with the stored
          hash set to anything at all.

          What that buys an attacker is laundering rather than forgery: the NEXT record links to this
          one's PayloadHash, so a run that accepted it would genuinely authenticate a link to a value
          of their choosing. Recomputing it here is the only place that can catch it.

          FALSIFIED by removing the Sha256 comparison from Check: this reddens on its own, and the
          authentication code alone still says the record is fine.
        */
        await WriteRowsAsync(2);
        var record = await AnchorAsync();

        record.PayloadHash = new string('a', 64);

        _anchors.Check(record).Should().Be(
            AuditAnchorCheck.MacMismatch,
            "the code covers the payload and this value is not in it, so nothing else would notice");
    }

    /*
      THE TWO ATTACKS ON THE ANCHOR CHAIN ITSELF LIVE ON SQL SERVER, not here, and the reason is the
      same one that put the row chain's tamper proofs there: removing or rewriting a record is
      something whoever holds a connection does, straight past the change tracker and the insert-only
      guard. The InMemory provider supports neither ExecuteDelete nor ExecuteUpdate, so a test written
      here could only perform the attack through the very funnel that refuses it -- which would prove
      the funnel works and say nothing about the chain.

      See AuditAnchorSqlServerTests: AnInteriorRecordRemoved_IsCaughtByWalkingTheWholeChain and
      AnUnauthenticRecordStopsTheWalkWhereItIS_NotAtTheTail.
    */

    [Fact]
    public async Task AnEmptyChainVerifies_BecauseNothingIsMissingFromNothing()
    {
        var state = await _anchors.VerifyChainAsync(_context);

        state.IsIntact.Should().BeTrue();
        state.Verified.Should().Be(0, "and the count says plainly that it proved nothing");
    }

    [Fact]
    public async Task DeletingAnAnchorThroughTheFunnelIsRefused_ThoughThatIsNotTheThreAT()
    {
        /*
          THE OTHER HALF OF INSERT-ONLY. Refusing updates without refusing deletes would leave the
          cheaper move open to our own future code.

          ⚠️ AND THE LIMIT, STATED SO NOBODY READS THIS AS A DEFENCE: the attacker this table exists
          for uses raw SQL and never passes through the change tracker. What a consistent suffix
          removal from BOTH chains actually does is pinned on SQL Server, where it can be performed
          the way it would really be performed.
        */
        await WriteRowsAsync(2);
        var record = await AnchorAsync();

        _context.Set<AuditAnchor>().Remove(record);
        var act = async () => await _context.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*insert-only*");
    }
}
