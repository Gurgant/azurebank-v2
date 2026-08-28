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
/// The one test that exercises the QUERY rather than the mapping.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ EVERY OTHER TEST IN THIS FILE HANDS <c>Report</c> AN <c>AnchorCoverage</c> DIRECTLY, so all of
/// them would stay green if <c>RunAsync</c> read the wrong column. They pin what the tool SAYS about
/// a coverage value; nothing there pins where the value comes from, and the choice of where it comes
/// from is the entire decision this feature turns on.
/// </para>
/// <para>
/// So this one builds the state end to end and runs the real command: nine rows anchored, four of
/// them removed, then anchored again. The newest record now honestly covers through the new tail,
/// and the deepest claim still reaches the old one. Newest-based arithmetic prints a clean zero over
/// a truncation; deepest-based arithmetic prints the gap.
/// </para>
/// </remarks>
public class UncoveredWindowQueryTests : IDisposable
{
    private const string ChainKey = "uncovered-window-tests-chain-key-0123456789";
    private const string AnchorKey = "uncovered-window-tests-anchor-key-987654321";

    private readonly ServiceProvider _services;
    private readonly AzureBankDbContext _context;

    /// <summary>
    /// Named rather than inline, so a second provider can reach the SAME store — which is what
    /// makes a gap marker reachable without reaching into the table by hand.
    /// </summary>
    private readonly string _store = Guid.NewGuid().ToString();

    public UncoveredWindowQueryTests()
    {
        var options = Options.Create(new AuditOptions { ChainKey = ChainKey, AnchorKey = AnchorKey });
        var chain = new AuditChain(options, NullLogger<AuditChain>.Instance);

        _context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(_store).Options,
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
                Event = $"WindowEvent{i}",
                Outcome = AuditOutcome.Succeeded,
                ActorUserId = Guid.NewGuid(),
                RowHash = string.Empty,
            });
            await _context.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task ATruncationFOLLOWEDByAnHonestAnchorIsStillVisible()
    {
        /*
          THE STATE THAT SEPARATES THE TWO COMPARANDS, built rather than argued about.

          Nine rows, anchored -- record 1 covers through sequence 9. Four rows removed. Anchored
          again -- record 2 honestly covers through sequence 5, because that is where the table now
          ends. The newest record and the table agree perfectly. The DEEPEST claim does not: an
          anchor once saw sequence 9, and sequence 9 is gone.

          Reading the newest record prints "at least 0 rows outside every anchor" and the truncation
          is invisible. Reading the maximum prints NEGATIVE and names the four missing sequences.

          FALSIFIED by changing MaxAsync in RunAsync to read the newest record's own coverage
          (OrderByDescending(a => a.AnchorSequence).First().CoveredThroughSequence): this reddens,
          and every Report test in this file stays green -- which is why this test exists.
        */
        await WriteRowsAsync(9);
        await AnchorCommand.RunAsync(_services, CancellationToken.None);

        var doomed = await _context.AuditEvents.OrderByDescending(e => e.Sequence).Take(4).ToListAsync();
        _context.AuditEvents.RemoveRange(doomed);
        await _context.SaveChangesAsync();

        await AnchorCommand.RunAsync(_services, CancellationToken.None);

        var (exitCode, lines) = await VerifyCommand.RunAsync(_services, CancellationToken.None);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(
            VerifyCommand.Intact,
            "the surviving rows still hash and link -- that is the whole reason truncation is hard");

        text.Should().Contain(
            "NEGATIVE", "an anchor saw sequence 9 and the table now ends at 5");
        text.Should().NotContain(
            "at least 0 rows", "which is what reading the newest record would have printed");
    }

    [Fact]
    public async Task WritingOverATruncationHEALSTheWindow_WhichIsWhyItIsNotADetector()
    {
        /*
          THE VERIFIER PRINTS THIS ABOUT ITSELF AND NOTHING ASSERTED IT. The window block tells the
          operator the number HEALS -- "sequences are reissued after a truncation, so writing enough
          new rows brings the tail back past the claim" -- and that sentence was shipped, repeated in
          docs/deferred/anchoring-the-audit-trail.md, and never held in place by anything. A comment
          that asserts a behaviour is a test not yet written; this is the test.

          It also matters more than the usual case for saying so, because the property it pins is a
          LIMIT rather than a capability. A limit nobody asserts is the kind of claim that quietly
          grows into "the window detects truncation" over a few edits, and the whole point of this
          number is that it does not.

          The state: nine rows, anchored -- record 1 covers through sequence 9. Four removed, so the
          table ends at 5. Four written back. AuditChain assigns Sequence from the TAIL it just read
          (row.Sequence = ++sequence), so the new rows take 6..9 -- the same numbers that were
          deleted -- and the deepest anchor claim of 9 matches a tail of 9 again. The NEGATIVE window
          its sibling test above asserts is gone, and nothing in the output remembers the truncation.

          ⚠️ WHAT IS AND IS NOT PROVIDER-INDEPENDENT HERE, stated exactly rather than waved at. The
          TAIL READ is branched: relational goes through FromSqlRaw with UPDLOCK/HOLDLOCK, InMemory
          through an ordered LINQ read. The ASSIGNMENT is not -- both branches return (Sequence,
          RowHash) of the last row and the shared line above assigns ++sequence from it. So the reuse
          this test rests on is the shared half, which is why it sits beside its InMemory sibling.
          What an InMemory test cannot speak for is the locking, and the locking is not what heals.
          This was read from the source, NOT run against SQL Server; a SQL-gated version would be the
          stronger form and does not exist.

          FALSIFIED by deleting the WriteRowsAsync(4) below: the assertion reddens on NEGATIVE, which
          is precisely what the sibling test asserts and this one must not.
        */
        await WriteRowsAsync(9);
        await AnchorCommand.RunAsync(_services, CancellationToken.None);

        var doomed = await _context.AuditEvents.OrderByDescending(e => e.Sequence).Take(4).ToListAsync();
        _context.AuditEvents.RemoveRange(doomed);
        await _context.SaveChangesAsync();

        await WriteRowsAsync(4);

        var (exitCode, lines) = await VerifyCommand.RunAsync(_services, CancellationToken.None);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(VerifyCommand.Intact);

        (await _context.AuditEvents.MaxAsync(e => e.Sequence)).Should().Be(
            9, "the deleted sequences are reissued, which is the mechanism the healing rests on");

        text.Should().NotContain(
            "NEGATIVE",
            "the tail is back level with the deepest claim, so the arithmetic no longer disagrees -- "
            + "the truncation happened and this number can no longer see it");
    }

    /// <summary>
    /// A provider whose ROW key is wrong while its ANCHOR key is right, which is what makes
    /// <c>anchor</c> write a gap marker over a healthy anchor chain.
    /// </summary>
    /// <remarks>
    /// <c>Build</c> sets <c>anchorable = verification.IsIntact &amp;&amp; verification.Verified &gt; 0</c>
    /// from the ROW walk, and a marker's coverage columns are null by construction. The ANCHOR key
    /// stays correct so the anchor chain still verifies and the record is appended rather than
    /// refused — which is the state under test, not a broken one.
    /// </remarks>
    private ServiceProvider WrongRowKey()
    {
        var options = Options.Create(new AuditOptions
        {
            ChainKey = "a-completely-different-row-chain-key-0123456789",
            AnchorKey = AnchorKey,
        });
        var chain = new AuditChain(options, NullLogger<AuditChain>.Instance);
        var collection = new ServiceCollection();
        collection.AddSingleton<IOptions<AuditOptions>>(options);
        collection.AddSingleton<IAuditChain>(chain);
        collection.AddSingleton<IAuditAnchorChain>(new AuditAnchorChain(options));
        collection.AddSingleton(new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(_store).Options,
            timeProvider: null,
            auditChain: chain));
        return collection.BuildServiceProvider();
    }

    [Fact]
    public async Task AGapMarkerOnTopOfAGoodAnchorDoesNotCryWolf()
    {
        /*
          THE OTHER DIRECTION, and the reason the maximum is right rather than merely safer. A run
          against a chain it cannot vouch for writes a GAP MARKER, whose coverage columns are null by
          construction -- so the NEWEST record covers nothing. Reading it would report every row as
          unanchored on a chain that is perfectly well anchored, and an operator who is cried wolf at
          stops reading the line.

          ⚠️ THE FIRST VERSION OF THIS TEST WROTE NO MARKER AND PROVED NOTHING. It anchored once over
          six rows and asserted the window was zero -- which it is under BOTH implementations, since
          with a single record the newest IS the deepest. Its comment claimed reading the newest
          would redden it. Measured, that mutation reddens exactly ONE test in the suite and this was
          not it: the evidence was in a run I had already done and read past.

          The marker is produced the way one really appears: the ROW chain fails to verify, so
          `anchor` has nothing it can vouch for and records a marker instead. The ANCHOR key stays
          correct, so the anchor chain verifies and the record is appended rather than refused.

          FALSIFIED by keeping the newest record's coverage instead of the maximum: the window
          reports EVERY row and this reddens -- now alongside the truncation test rather than
          leaving it alone.
        */
        await WriteRowsAsync(6);
        await AnchorCommand.RunAsync(_services, CancellationToken.None);

        using (var wrongKey = WrongRowKey())
        {
            await AnchorCommand.RunAsync(wrongKey, CancellationToken.None);
        }

        var records = await _context.Set<AuditAnchor>().OrderBy(a => a.AnchorSequence).ToListAsync();
        records.Should().HaveCount(2, "the setup is the state under test and is checked, not assumed");
        records[0].CoveredThroughSequence.Should().Be(6, "the first record anchored the six rows");
        records[1].Kind.Should().Be(AuditAnchorKind.GapMarker, "and the second could vouch for none");
        records[1].CoveredThroughSequence.Should().BeNull("a marker covers nothing by construction");

        var (_, lines) = await VerifyCommand.RunAsync(_services, CancellationToken.None);
        var text = string.Join(" ", lines);

        text.Should().Contain(
            "at least 0 rows", "the DEEPEST record still reaches the tail, marker or no marker");
        text.Should().NotContain(
            "EVERY row", "which is what reading the newest record would have printed");
    }
}

/// <summary>
/// The uncovered window: how many rows sit above the deepest thing any anchor claims to cover.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ THIS NUMBER GETS MORE SCRUTINY THAN ITS SIZE SUGGESTS, and the reason is that it fails
/// QUIETLY. Every other verdict this tool produces is loud when it is wrong — a broken chain names a
/// sequence, a misconfiguration names a key, a refused export names a path. A wrong window is a
/// plausible integer. No test catches it unless a test is written for it, no reviewer recomputes it,
/// and an operator reads it as gospel. So every branch below is a separate fact rather than a
/// parameterised sweep, and each names the state that produces it.
/// </para>
/// <para>
/// These assert against <see cref="VerifyCommand.Report"/>, which is a side-effect-free static — the
/// whole verdict-to-text mapping is exercised with no database, no console and no process exit, the
/// way the three existing outcomes already are.
/// </para>
/// </remarks>
public class UncoveredWindowTests
{
    private static AuditChainVerification Intact(long rows, long lowest, long highest) =>
        new(rows, null, null, lowest, highest);

    [Fact]
    public void The_deepest_coverage_is_the_comparand_not_the_newest_records_own()
    {
        /*
          THE DECISION THE WHOLE THING TURNS ON. Truncate the rows, then run `anchor` again: the
          newest record honestly covers through the NEW tail, so newest-based arithmetic prints a
          clean zero over exactly the attack this train exists to notice. The deepest claim ever made
          is what the current tail has to answer to, and it is the only comparand that survives being
          followed by an honest anchor.

          Here: anchors once reached sequence 900, the table now ends at 500. That is a truncation
          somebody did not clean up after.

          FALSIFIED by taking the coverage from the newest record instead of the maximum: the window
          reads 0 and this reddens.
        */
        var (exitCode, lines) = VerifyCommand.Report(
            Intact(500, 1, 500), 1, 500,
            new VerifyCommand.AnchorCoverage(ChainVerified: true, DeepestCovered: 900, Records: 4));

        exitCode.Should().Be(VerifyCommand.Intact, "the ROW chain is intact; the anchors disagree");
        var text = string.Join(" ", lines);
        text.Should().Contain("NEGATIVE");
        text.Should().Contain("400", "900 minus 500 sequences an anchor says it saw are gone");
        text.Should().Contain("escalate");
    }

    [Fact]
    public void A_negative_window_is_never_clamped_to_zero()
    {
        /*
          Clamping would delete the one finding this arithmetic can make on its own. A window below
          zero means the anchors claim coverage through a sequence that no longer exists, and nothing
          legitimate produces it -- rows are only ever appended, so the tail only moves up.

          FALSIFIED by wrapping the subtraction in Math.Max(0, ...): the text loses NEGATIVE and this
          reddens.
        */
        var (_, lines) = VerifyCommand.Report(
            Intact(10, 1, 10), 1, 10,
            new VerifyCommand.AnchorCoverage(true, 5_000, 12));

        var text = string.Join(" ", lines);
        text.Should().Contain("NEGATIVE");
        text.Should().NotContain("at least 0 rows", "zero is what clamping would have printed");
    }

    [Fact]
    public void The_negative_case_says_what_it_cannot_see()
    {
        /*
          A number that catches one version of an attack invites being read as catching the attack.
          It does not: sequences are REISSUED after a truncation, so writing enough new rows brings
          the tail back past the claim and the window heals itself; and deleting the covering anchors
          as well drops the claim with the tail, which is the limit
          ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit already asserts on
          SQL Server.

          FALSIFIED by dropping WhatTheWindowCannotSee from the negative branch.
        */
        var (_, lines) = VerifyCommand.Report(
            Intact(500, 1, 500), 1, 500,
            new VerifyCommand.AnchorCoverage(true, 900, 4));

        var text = string.Join(" ", lines);
        text.Should().Contain("HEALS", "the signal is loud only while the table is short of the claim");
        text.Should().Contain("ConsistentSuffixRemovalFromBOTHChains", "the thorough version is invisible");
    }

    [Fact]
    public void An_empty_audit_table_beside_a_live_anchor_is_where_it_is_LOUDEST()
    {
        /*
          ⚠️ THE BRANCH THAT USED TO RETURN BEFORE THE WINDOW EXISTED. An empty table beside an anchor
          claiming coverage through 5,000 is a table truncated to NOTHING with the anchors left
          behind -- the most complete tamper there is. The verdict's own text says "a table truncated
          to nothing reports exactly what a fresh one does", and that stops being true the moment
          there is an anchor to disagree with it.

          FALSIFIED by returning from the NothingToVerify branch before calling UncoveredWindow, which
          is how it was first written: the loudest case goes silent and this reddens.
        */
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(0, null, null), null, null,
            new VerifyCommand.AnchorCoverage(true, 5_000, 9));

        exitCode.Should().Be(VerifyCommand.NothingToVerify, "the verdict about the chain is unchanged");
        var text = string.Join(" ", lines);
        text.Should().Contain("NOTHING TO VERIFY", "the existing sentence stays");
        text.Should().Contain("NEGATIVE", "and the anchors now contradict it out loud");
        text.Should().Contain("5,000");
    }

    [Fact]
    public void An_empty_table_with_no_anchors_says_every_row_rather_than_a_number()
    {
        /*
          Nothing to compare, and "0 rows are outside every anchor" would be the most dangerous
          sentence this command could print: literally true of an empty table, and read as "you are
          covered". The absence of a comparand is the finding.

          FALSIFIED by treating a missing anchor as coverage through sequence 0.
        */
        var (_, lines) = VerifyCommand.Report(
            new AuditChainVerification(0, null, null), null, null,
            new VerifyCommand.AnchorCoverage(true, null, 0));

        var text = string.Join(" ", lines);
        text.Should().Contain("EVERY row");
        text.Should().Contain("No anchor has ever been recorded");
        text.Should().NotContain("at least 0");
    }

    [Fact]
    public void Anchors_that_all_cover_NOTHING_are_not_the_same_as_anchors_that_cover()
    {
        /*
          A run against an empty or broken chain writes a GAP MARKER, whose coverage columns are null
          BY CONSTRUCTION. A deployment that only ever ran `anchor` while the table was empty has a
          populated anchor table covering nothing at all -- and a populated table is what "we have
          anchors" looks like from the outside. The count is carried separately from the coverage for
          exactly this: to tell an operator which of the two they have.

          FALSIFIED by folding Records into the DeepestCovered null check: both states print the same
          sentence and the operator cannot tell them apart.
        */
        var (_, lines) = VerifyCommand.Report(
            Intact(300, 1, 300), 1, 300,
            new VerifyCommand.AnchorCoverage(true, null, 6));

        var text = string.Join(" ", lines);
        text.Should().Contain("EVERY row");
        text.Should().Contain("6", "the operator is told records exist");
        text.Should().Contain("gap marker", "and told why they cover nothing");
    }

    [Fact]
    public void An_unverified_anchor_chain_produces_NO_number_at_all()
    {
        /*
          A chain with a record missing or mis-linked can claim any coverage at all, so arithmetic on
          it would produce a plausible integer with nothing behind it -- which is this whole file's
          failure mode. The absence is stated rather than left as a blank.

          FALSIFIED by computing the window regardless of ChainVerified.
        */
        var (_, lines) = VerifyCommand.Report(
            Intact(300, 1, 300), 1, 300,
            new VerifyCommand.AnchorCoverage(ChainVerified: false, DeepestCovered: 100, Records: 3));

        var text = string.Join(" ", lines);
        text.Should().Contain("not computed");
        text.Should().NotContain("at least 200", "which is what the arithmetic would have said");
    }

    [Fact]
    public void The_ordinary_case_is_a_LOWER_bound_and_says_so_twice()
    {
        /*
          The number exists to turn "I do not know how much is unanchored" into a quantity. It is a
          LOWER bound because rows can be appended while the walk runs, and it is not an upper bound
          on anything because nothing here schedules an anchor -- the cadence is human, so the window
          has no ceiling and a small number now says nothing about tomorrow.

          Both halves are asserted because the number is worthless, and worse than worthless, if it
          is read as a guarantee.

          FALSIFIED by printing the bare count without the qualifying lines.
        */
        var (exitCode, lines) = VerifyCommand.Report(
            Intact(1_200, 1, 1_200), 1, 1_200,
            new VerifyCommand.AnchorCoverage(true, 1_000, 3));

        exitCode.Should().Be(VerifyCommand.Intact);
        var text = string.Join(" ", lines);
        text.Should().Contain("at least 200 rows are outside every anchor");
        text.Should().Contain("AT LEAST, because rows can be appended");
        text.Should().Contain("no ceiling");
        text.Should().Contain("A missing anchor is not evidence");
    }

    [Fact]
    public void A_disagreeing_COUNT_is_named_as_the_finding_rather_than_left_as_a_puzzle()
    {
        /*
          THE UNIT AND THE INSTRUCTION HAVE TO MEET. The window is arithmetic in SEQUENCE space, and
          the intact verdict three lines above already tells the operator to "compare the count
          against your own". Follow both and a key-holding interior deletion produces two numbers
          that disagree -- the span says 100, their COUNT(*) says 69 -- with nothing saying which to
          believe. The likeliest conclusion from a tool that contradicts itself is that the tool is
          broken, which is worse than either number, and it would be reached in exactly the incident
          the numbers exist for.

          So the disagreement is named as the finding. It is the only trace that deletion leaves:
          VerifyAsync checks the link, the payload version, the key identity and the hash, and never
          the contiguity, so the chain still reads intact with a hole in its numbering.

          FALSIFIED by trimming the caveat back to naming the unit: the operator is told the number
          is in sequences and left to work out what a mismatch means.
        */
        var (_, lines) = VerifyCommand.Report(
            Intact(1_200, 1, 1_200), 1, 1_200,
            new VerifyCommand.AnchorCoverage(true, 1_000, 3));

        var text = string.Join(" ", lines);
        /*
          ASSERTED WITHIN ONE LINE EACH, because these lines are printed separately and joining them
          for the assertion inserts the next line's indentation. The first draft asserted across a
          break and reddened on whitespace rather than on meaning -- a test failing for a reason the
          reader has to squint at is a test that will be deleted rather than fixed.
        */
        text.Should().Contain("SEQUENCE NUMBERS", "the unit is named");
        text.Should().Contain(
            "not a fault in this tool",
            "a disagreement is diagnosed rather than left as a contradiction");
        text.Should().Contain(
            "the one trace a key-holding interior", "and attributed to the adversary that leaves it");
        text.Should().Contain("Compare the count against your own", "the instruction it answers");
    }

    [Fact]
    public void A_window_of_zero_is_stated_as_a_bound_rather_than_as_coverage()
    {
        /*
          Zero is the number most likely to be misread as "everything is anchored". It is not: it
          means the deepest claim reaches the tail AT THIS INSTANT, which a single row written a
          moment later undoes. The wording keeps "at least" so the reading survives the number.

          FALSIFIED by special-casing zero into a reassuring sentence.
        */
        var (_, lines) = VerifyCommand.Report(
            Intact(700, 1, 700), 1, 700,
            new VerifyCommand.AnchorCoverage(true, 700, 2));

        var text = string.Join(" ", lines);
        text.Should().Contain("at least 0 rows");
        text.Should().Contain("AT LEAST, because rows can be appended");
    }

    [Fact]
    public void A_broken_verdict_computes_NO_window_and_says_why()
    {
        /*
          On a break, HighestSequence is where the walk STOPPED, not where the chain ends --
          subtracting the anchor's coverage from it produces a number about a prefix, printed in the
          same words as a number about the whole table. There is no safe direction for that error, so
          there is no number, and the absence is named rather than left to be noticed.

          FALSIFIED by appending UncoveredWindow to the broken branch: it prints a window computed
          against the break position.
        */
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(
                40, 41, "Row 41 does not link to its predecessor", 1, 41,
                AuditChainBreakKind.LinkBroken),
            1, 41,
            new VerifyCommand.AnchorCoverage(true, 20, 3));

        exitCode.Should().Be(VerifyCommand.Broken);
        var text = string.Join(" ", lines);
        text.Should().Contain("UNCOVERED WINDOW: not computed");
        text.Should().Contain("describe a prefix");
        text.Should().NotContain("at least 21", "which is what the arithmetic would have said");
    }
}
