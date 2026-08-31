using System.CommandLine;
using System.Data.Common;
using System.CommandLine.Invocation;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureBank.AuditVerifier.Commands;

/// <summary>
/// Walks the whole chain and says what it found -- with the count, because "intact" alone is not
/// an answer.
/// </summary>
public static class VerifyCommand
{
    /// <summary>Every row hashed and linked, and there was something to check.</summary>
    public const int Intact = 0;

    /// <summary>A row does not hash or does not link. The sequence is reported.</summary>
    public const int Broken = 1;

    /*
      NOTHING TO VERIFY IS ITS OWN EXIT CODE, not a success.

      VerifyAsync reports IsIntact for an empty table, which is true and useless: a chain of zero
      rows links perfectly. This project has already shipped a test that passed because it verified
      nothing, so the tool refuses to render that as a green result. An operator running this after
      an incident needs to know the difference between "the chain is whole" and "there was no chain
      to look at" -- the second can mean the table was truncated to nothing.
    */
    public const int NothingToVerify = 2;

    /// <summary>
    /// The tool could not start: a missing or malformed Audit:ChainKey, or an unreachable database.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Broken"/> on purpose. Both mean "no verdict", but one is a fact
    /// about the bank and the other is a fact about this invocation, and a script that cannot tell
    /// them apart will page somebody for a typo in an environment variable.
    /// </remarks>
    public const int Misconfigured = 3;

    /// <summary>
    /// The verdict for a ring that cannot be built, shared by all three verbs.
    /// </summary>
    /// <remarks>
    /// ⚠️ SHARED BECAUSE IT WAS NOT, AND THE TWO VERBS THAT LACKED IT ANSWERED 4. The ring's rules
    /// are enforced in <see cref="AuditChain"/>'s constructor, so they surface wherever a caller
    /// happens to resolve the chain — inside <c>verify</c>'s try, one line ABOVE the try in
    /// <c>anchor</c> and <c>export</c>. Measured with one short retired key: <c>verify</c> 3 with
    /// prose, the other two <b>4</b> with an unhandled stack trace, which this tool's own scale
    /// defines as "the command line was wrong" while the command line was right.
    /// <para>
    /// The runbook records the identical defect from an earlier release, same two verbs, and closes
    /// *"Both now answer 3, like `verify`."* — so this is that incident re-opened by the key ring,
    /// and a verdict in one place is what stops it re-opening a third time.
    /// </para>
    /// </remarks>
    internal static (int ExitCode, string[] Lines) RingNotConfigured(AuditKeyRingException failure) =>
    (
        Misconfigured,
        [
            "CANNOT PROCEED: this tool is not configured to read the chain.",
            $"  {failure.Message}",
            "  NOTHING WAS READ. The key ring is checked when the chain is BUILT, so this is a",
            "  statement about the configuration and not about the audit table -- do not treat it",
            "  as a finding about the data. Exit 3, the same code a missing key produces, because",
            "  it is the same kind of problem.",
        ]);

    /// <summary>
    /// The command line itself was wrong: no command, a mistyped one, an unknown option.
    /// </summary>
    /// <remarks>
    /// EXISTS BECAUSE THE FRAMEWORK COLLIDES WITH THIS TOOL'S VOCABULARY. System.CommandLine's
    /// default pipeline reports every parse failure as exit <b>1</b>, and 1 here means CHAIN BROKEN.
    /// Measured on the pinned 2.0.0-beta4: running the tool with NO ARGUMENTS AT ALL printed
    /// "Required command was not provided." and exited 1, as did a mistyped command and an unknown
    /// option. The most likely mistake anyone can make with this tool reported a tampered audit
    /// trail. Program.cs translates the framework's 1 into this.
    /// </remarks>
    public const int UsageError = 4;

    /// <summary>The walk was interrupted before it could reach a verdict.</summary>
    /// <remarks>
    /// <para>
    /// SEPARATE FROM <see cref="Misconfigured"/> because the tools that do this for a living keep
    /// them apart, and because folding them had already produced a defect here: the runbook glosses
    /// 3 as "the store could not be read" and tells an operator to wire an alert on it with a triage
    /// list of environment failures, none of which applies to somebody pressing Ctrl+C.
    /// </para>
    /// <para>
    /// <c>e2fsck</c> documents <b>32</b> as "canceled by user request", distinct from 8 "operational
    /// error"; AIDE documents <b>25</b> for SIGINT/SIGTERM/SIGHUP, distinct from 18 (IO) and 24
    /// (database). Of the comparable verifiers only tripwire folds everything into one catch-all.
    /// Five rather than 130: 128+signal is a SHELL-side encoding of a process killed BY a signal,
    /// and this process catches the interruption and exits deliberately.
    /// </para>
    /// </remarks>
    public const int Interrupted = 5;

    /// <summary>
    /// What the anchor table says it covers, observed before the row walk began.
    /// </summary>
    /// <param name="ChainVerified">
    /// Whether the anchor chain itself verified. False means the numbers below are not worth
    /// arithmetic: a chain with a hole in it can claim anything.
    /// </param>
    /// <param name="DeepestCovered">
    /// The HIGHEST <c>CoveredThroughSequence</c> across every record — not the newest record's own.
    /// Null when no record covers anything.
    /// </param>
    /// <param name="Records">How many anchor records exist at all.</param>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>THE DEEPEST COVERAGE, NOT THE NEWEST RECORD'S, AND THE DIFFERENCE IS THE WHOLE POINT.</b>
    /// Two states separate them and the tool must survive both. A gap marker written over a healthy
    /// history is the newest record and covers NOTHING — its coverage columns are null by
    /// construction — so reading the newest would report every row as unanchored and cry wolf. Worse
    /// in the other direction: truncate the rows, then run <c>anchor</c> again, and the newest record
    /// honestly covers through the NEW tail. Newest-based arithmetic then prints zero rows
    /// uncovered — silence over exactly the attack this whole train exists to notice. The deepest
    /// claim ever made is the one the current tail has to answer to.
    /// </para>
    /// <para>
    /// A record can be deleted to lower that maximum, and the anchor chain catches only half of
    /// that: an INTERIOR removal gaps the counter and breaks a link, which <c>ChainVerified</c>
    /// above is what looks for, but a SUFFIX removal leaves 1..n intact and takes the maximum down
    /// with it in silence. That is the same limit the row chain has, and it is why this number is a
    /// lower bound rather than a measurement.
    /// </para>
    /// </remarks>
    public readonly record struct AnchorCoverage(bool ChainVerified, long? DeepestCovered, long Records);

    /// <summary>
    /// Turns a verification result into what the operator sees and what a script reads.
    /// </summary>
    /// <remarks>
    /// Separated from the command so the three outcomes can be asserted without a console, a
    /// database or a process exit. The mapping IS the tool -- the rest is plumbing -- and an
    /// untested mapping is how "intact" ends up printed over an empty table.
    /// </remarks>
    public static (int ExitCode, IReadOnlyList<string> Lines) Report(
        AuditChainVerification result, long? lowest, long? highest, AnchorCoverage coverage = default)
    {
        /*
          BROKEN IS CHECKED BEFORE EMPTY, and the order is not cosmetic.

          A chain that fails on its FIRST row has verified nothing, so Verified is 0 -- the same
          value an empty table produces. Checking the count first reported "NOTHING TO VERIFY" for a
          broken chain and made the wrong-key hint below unreachable, which is exactly the case it
          was written for. Caught by AuditVerifierReportTests, not by reading.
        */
        if (!result.IsIntact)
        {
            var lines = new List<string>
            {
                $"CHAIN BROKEN at sequence {result.FirstBrokenSequence:N0}.",
                $"  {result.Reason}",
                $"  Rows verified before the break: {result.Verified:N0}",
                lowest is null
                    ? "  Sequences read: none -- the walk did not get past the first row"
                    : $"  Sequences read: {lowest:N0} to {highest:N0}",
            };

            /*
              SUSPECT THE KEY BEFORE SUSPECTING AN ATTACKER, when the break is on the FIRST ROW READ.

              THE COUNT AND THE POSITION, and an earlier round of this file got that wrong in the
              dangerous direction. It dropped the position on the argument that Sequence is tail + 1
              and never restarts, so a purged chain begins at 5,001 and a position test would stop
              firing on the oldest tables. THAT SCENARIO CANNOT HAPPEN. VerifyAsync checks the LINK
              before the hash, so the only row that can reach the hash check first is one recording
              no predecessor -- and Link writes that only when tail is null, i.e. into an empty
              table, where the row it writes is sequence 1. On a chain whose head is gone the first
              row read records a predecessor that is missing, so the walk reports LinkBroken and the
              hash check is never reached. Measured: a WRONG key against a decapitated chain prints
              output identical to the correct key.

              What the loosened gate did produce was an exoneration. Deleting the oldest rows and
              clearing the survivor's PreviousHash -- the cheapest way to hide a deleted prefix --
              gives HashMismatch with Verified == 0 above sequence 1, and the tool answered
              "usually means the wrong Audit:ChainKey ... Confirm the key before escalating" WITH
              THE CORRECT KEY IN USE, while printing "Sequences read: 2 to 2" on the line above it.
              Measured on SQL Server before this branch existed.

              AND ONLY FOR A HASH MISMATCH, which is the only break a wrong key can cause. It cannot
              make a row unreadable, and it cannot change what a row records as its predecessor -- so
              on a deleted prefix or a poisoned column the hint was sending an operator to check a
              key that was never the problem. Measured on both.

              AND ONLY ON A ROW THAT RECORDS NO KEY IDENTITY, which is what makes the paragraph below
              a 'v2'-only statement now. A row that names its key is checked against that name BEFORE
              its hash is recomputed, so a wrong key there never reaches the hash: it reports
              UnknownScheme instead. Printing "confirm the key" on a hash mismatch over such a row
              would be exoneration in reverse -- the tool would send an operator to re-check a key it
              had just proved correct, while a genuine write went unescalated.

              DO NOT REMOVE THE SEQUENCE GATE ON THIS HINT. The row hash is an HMAC over
              Audit:ChainKey, and unlike a missing or short key a wrong one passes the options
              validation, because it is a perfectly well-formed secret. What it is not is the one
              this chain was written with.

              ON A ROW RECORDING NO KEY IDENTITY that is indistinguishable from tampering by any
              check this tool can make, which is what the hint is for. It is no longer true in
              general: a row that names its key is refused BY NAME before its hash is recomputed, so
              a wrong key there yields an unchecked row rather than a mismatch -- and a mismatch on
              such a row therefore rules the key OUT.

              The tell is the position: a real tamper breaks WHERE it happened, somewhere in the
              table. A wrong key breaks at the first row every time, because the first hash it
              recomputes is already different. Saying so here costs nothing and saves an operator
              from opening an incident about an attacker who does not exist.
            */
            if (result.Verified == 0 && result.Kind == AuditChainBreakKind.HashMismatch)
            {
                // GATED ON THE RECORDED IDENTITY, not on a version string. A literal "v3" here
                // would duplicate AuditChain.CurrentPayloadVersion across an assembly boundary with
                // nothing tying the two together, and it would need updating on every future
                // version. What decides the advice is whether the row named a key at all.
                if (result.FirstBrokenSequence == 1 && result.RecordedKeyId is null)
                {
                    // NOT Audit:ChainKey. This branch fires on a row recording NO key identity,
                    // and those are checked under Audit:FoundingChainKey -- which is Audit:ChainKey
                    // only while nothing has been retired. Naming the current key here sends an
                    // operator to a key that never touched the row: the same defect raised in
                    // review on 9e92377 and corrected on fc1c496, in the sibling arm below, and
                    // missed here. (This comment named 9e92377 as the CORRECTION until it was
                    // checked -- that commit never touched this file. Raised on / corrected on are
                    // different commits and this corpus keeps the two phrasings apart.)
                    lines.Add("  Breaking at sequence 1 usually means the wrong key, not tampering");
                    lines.Add("  -- a wrong key is well-formed, so validation cannot catch it. This");
                    lines.Add("  row records no key identity, so the key applied to it is");
                    lines.Add("  Audit:FoundingChainKey, which is Audit:ChainKey only while nothing");
                    lines.Add("  has been retired.");
                    lines.Add("  Confirm the key before escalating.");
                }
                else if (result.FirstBrokenSequence == 1)
                {
                    // The opposite conclusion, and the stronger statement this tool could never make
                    // before: the key behind this row has already been confirmed by its own id.
                    lines.Add("  The key is CONFIRMED for this row: it records a key id, the");
                    lines.Add("  configured ring SELECTED the key by that id, and a key the ring");
                    lines.Add("  cannot select never reaches a hash comparison. After a rotation");
                    lines.Add("  that key is usually a RETIRED one, not Audit:ChainKey -- so this");
                    lines.Add("  is a WRITE, not a key problem. Preserve the table and escalate.");
                }
                else
                {
                    lines.Add("  This is NOT the key. A row above sequence 1 that records no");
                    lines.Add("  predecessor was WRITTEN that way, which is how a deleted prefix is");
                    lines.Add("  hidden. Preserve the table and escalate.");
                }
            }

            /*
              A LINK BREAK BEFORE ANYTHING VERIFIES MEANS TWO DIFFERENT THINGS, and the sequence
              separates them. The walk starts from "(start of chain)", so it breaks here when the
              first row read records a predecessor. If that row is sequence 1 it IS the start of the
              chain -- Link writes null there -- so the value it carries was written onto it, and
              nothing was removed. Above sequence 1 the rows beneath it are gone.

              Measured on SQL Server, same intact chain of three: writing a PreviousHash onto row 1
              gives "CHAIN BROKEN at sequence 1 ... Sequences read: 1 to 1" with all three rows still
              present; deleting row 1 gives "CHAIN BROKEN at sequence 2 ... Sequences read: 2 to 2".
              The runbook said this verdict meant the oldest rows were gone, which is false for the
              first of those and was the reading an operator would have acted on.
            */
            if (result.Verified == 0 && result.Kind == AuditChainBreakKind.LinkBroken)
            {
                if (result.FirstBrokenSequence == 1)
                {
                    lines.Add("  NOTHING was removed from the head: this IS the start of the chain,");
                    lines.Add("  so the predecessor it records was written onto it. Only an update");
                    lines.Add("  does that. Preserve the table and escalate.");
                }
                else
                {
                    lines.Add("  The rows BELOW this sequence are gone. An archival job and an");
                    lines.Add("  attacker print this same line, so establish which before you");
                    lines.Add("  repair anything.");
                }
            }

            /*
              UnknownScheme GATES ON THE KIND ALONE, never on Verified == 0. A table holding both
              renderings verifies its legacy prefix first, so a verifier holding the wrong key
              surfaces here with Verified > 0 -- and a Verified == 0 gate would print nothing at all
              in exactly the case an operator most needs the reading.
            */
            if (result.Kind == AuditChainBreakKind.UnknownScheme)
            {
                /*
                  SEVEN CAUSES, AND THIS COMMENT HAS SAID THREE, THEN FIVE, THEN SIX. Each time the
                  number moved, this paragraph -- the one that ARGUES for the number -- was left
                  behind by a commit that changed the strings below it. That is the branch's own
                  defect committed against its own explanation, so the count is now derived here
                  rather than remembered:

                  The EIGHT paths, each a distinct return or switch arm in VerifyAsync, in the order the
                  walk reaches them:
                    1. the payload version cannot be rendered by this build;
                    2. a 'v2' row carries a key id, which that version has nowhere to keep;
                    3. no key in the ring has the row's id;
                    4. the ring holds the key, the row is ABOVE its epoch;
                    5. the ring holds the key, the row is BELOW its epoch;
                    6. the row records no id and is ABOVE Audit:FoundingChainKey's epoch;
                    7. the row records no id and is BELOW it;
                    8. the row declares the current version and carries no id at all.

                  Eight paths, SEVEN printed causes. Paths 2 and 8 share a bullet because they share
                  an action -- the identity column contradicts the version, so the value was written
                  after the fact -- and an operator does nothing different on the two. Every other
                  path takes a different action.

                  ⚠️ THE LIST ENUMERATES CAUSES, NOT READINGS. "The column was overwritten" used to
                  sit among them as a peer; it is not something the walk can return, it is how
                  several of them come about, so it is stated below the list instead.
                */
                lines.Add($"  This row declares payload version '{result.PayloadVersion ?? "(none)"}' and key id");
                lines.Add($"  '{result.RecordedKeyId ?? "(none)"}'. The CURRENT key's id is");
                lines.Add($"  '{result.ConfiguredKeyId ?? "(none)"}' -- and the ring may hold retired");
                lines.Add("  keys besides it, so those two differing is not by itself the problem.");
                lines.Add("  The hash was NOT checked, so this is a row left UNVERIFIED, never a");
                lines.Add("  row proved good.");
                lines.Add("  SEVEN causes produce this verdict. The SECOND line of this report --");
                lines.Add("  the one directly under CHAIN BROKEN -- says which one. They are:");
                lines.Add("    - no key in the ring has this row's id;");
                lines.Add("    - the ring HAS that key, but the row sits ABOVE the epoch it closes;");
                lines.Add("    - the ring HAS that key and the row sits BELOW the epoch it opens,");
                lines.Add("      so an earlier key wrote this stretch;");
                lines.Add("    - the row records no key id, so Audit:FoundingChainKey answers for");
                lines.Add("      it, and the row sits ABOVE that key's epoch;");
                lines.Add("    - the row records no key id and sits BELOW that key's epoch, which");
                lines.Add("      only a founding designation other than the OLDEST key can produce;");
                lines.Add("    - the row records a key id on a payload version that has no place");
                lines.Add("      to keep one, or records none on a version that has;");
                lines.Add("    - this build cannot render the version the row declares.");
                lines.Add("  Each applies to an INTERVAL -- the epoch of the key it concerns --");
                lines.Add("  and this walk stopped at the first row of it. A key missing from");
                lines.Add("  Audit:RetiredChainKeys therefore breaks in the MIDDLE of the table,");
                lines.Add("  with verified rows beneath it, which is what a single overwritten row");
                lines.Add("  looks like too. To tell them apart, add the id above to");
                lines.Add("  Audit:RetiredChainKeys with the boundary from the rotation record and");
                lines.Add("  verify again: a configuration miss clears, a write does not.");
                lines.Add("  ⚠️ AN OVERWRITTEN COLUMN PRODUCES SEVERAL OF THESE and is not one more");
                lines.Add("  entry in the list: PayloadVersion and KeyId are inside the hashed");
                lines.Add("  payload, so changing either is a modification that then surfaces as");
                lines.Add("  whichever of the causes above it happens to trip.");
                lines.Add("  FOUR of the seven are boundary causes -- above and below, for a row");
                lines.Add("  that names a key and for one that does not. Three have MINTING as");
                lines.Add("  their alternative; the fourth, an identity-less row BELOW the founding");
                lines.Add("  epoch, does not -- there the fix is the DESIGNATION. A boundary edit");
                lines.Add("  moves that start but cannot lower it past the designation's POSITION");
                lines.Add("  in boundary order, so on a trail that begins at sequence 1 it never");
                lines.Add("  clears. Read the runbook before touching the configuration.");
                lines.Add("  TWO of the seven are fixed in configuration: a missing ring entry,");
                lines.Add("  and the DESIGNATION for an identity-less row below the founding");
                lines.Add("  epoch. The rest are not, and no reading here makes this a row proved");
                lines.Add("  good. Treat it as a break.");
            }

            lines.Add("  Do NOT repair by deleting rows: see docs/runbooks/audit-chain-unavailable.md");
            /*
              THE WINDOW IS NOT COMPUTED ON A BROKEN VERDICT, and the absence is stated rather than
              left to be noticed. HighestSequence on a break is where the walk STOPPED, not where the
              chain ends -- subtracting the anchor's coverage from it would produce a number about a
              prefix, presented in the same words as a number about the whole table. There is no safe
              direction for that error, so there is no number.
            */
            lines.Add(string.Empty);
            lines.Add("UNCOVERED WINDOW: not computed. On a broken verdict the walk stops at the");
            lines.Add("  break, so the highest sequence it reached is not the end of the table and");
            lines.Add("  arithmetic against it would describe a prefix. Fix the break first.");

            return (Broken, lines);
        }

        if (result.Verified == 0)
        {
            /*
              ⚠️ THE WINDOW IS LOUDEST HERE AND THIS BRANCH USED TO RETURN BEFORE IT. An empty table
              beside an anchor claiming coverage through sequence 5,000 is a table truncated to
              NOTHING with the anchors left behind -- the most complete tamper there is, and the one
              state where the arithmetic speaks on its own. Returning early made the tool silent in
              exactly the case its own text calls out: "a table truncated to nothing reports exactly
              what a fresh one does". It does not, once there is an anchor to disagree with it.

              The tail is 0 here rather than null, which is what makes the negative branch below fire.
            */
            return (NothingToVerify,
            [
                "NOTHING TO VERIFY: the audit table has no rows.",
                "  This is not the same as an intact chain. An empty chain links perfectly,",
                "  so a table truncated to nothing reports exactly what a fresh one does.",
                string.Empty,
                .. UncoveredWindow(coverage, 0),
            ]);
        }

        return (Intact,
        [
            $"CHAIN INTACT: {result.Verified:N0} rows verified.",
            $"  Sequence range: {lowest:N0} to {highest:N0}",
            "  This proves no row was altered by anyone who does not hold the key whose",
            "  EPOCH that row falls in -- Audit:ChainKey for everything since the last",
            "  retirement, and each retired key for its own stretch and no other -- and",
            "  that none was removed from the MIDDLE. Retiring a key narrows what a",
            "  verification ACCEPTS from it, never what it can write: inside its own epoch",
            "  it still rewrites and recomputes as freely as it ever did.",
            // "Compare the count against your own" STAYS ON ONE LINE. The uncovered-window text
            // below points at it by name -- "the verdict above already tells you to compare
            // counts" -- and UncoveredWindowTests asserts the phrase is contiguous, which is what
            // caught the rewrap that split it while every word was still on the screen.
            "  It does NOT prove none was removed from the END -- truncation needs no key and",
            "  leaves every surviving row linking correctly.",
            "  Compare the count against your own.",
            string.Empty,
            .. UncoveredWindow(coverage, highest),
        ]);
    }

    /// <summary>
    /// How many rows sit above the deepest thing any anchor claims to cover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS TURNS "I DO NOT KNOW HOW MUCH IS UNANCHORED" INTO A QUANTITY, which is the only half of
    /// freshness this deployment can honestly produce. Nothing here runs unattended, so nothing can
    /// demonstrate a cadence — but the gap itself is computable from local data, and a number an
    /// operator can watch move is worth more than a paragraph they cannot.
    /// </para>
    /// <para>
    /// ⚠️ <b>IT IS A LOWER BOUND AND IT IS NOT AN UPPER BOUND ON ANYTHING.</b> A lower bound because
    /// rows can be appended while the walk is still running, so the tail it is measured against may
    /// already be behind. Not an upper bound because the cadence is HUMAN: nothing schedules an
    /// anchor, so the window has no ceiling, and a small number today says nothing about tomorrow.
    /// Every sentence below is written to stop the number being read as a guarantee.
    /// </para>
    /// <para>
    /// ⚠️ <b>A NEGATIVE WINDOW IS NOT AN ERROR TO CLAMP.</b> It means the anchors claim coverage
    /// through a sequence that no longer exists, which is what a tail truncation looks like when
    /// whoever did it left the anchor records behind. Clamping it to zero would delete the one
    /// finding this arithmetic can make on its own.
    /// </para>
    /// </remarks>
    private static string[] UncoveredWindow(AnchorCoverage coverage, long? highest)
    {
        /*
          ⚠️ THE ARITHMETIC IS IN SEQUENCE SPACE, AND THAT IS A ROW COUNT ONLY BY ASSUMPTION.
          `Sequence` is assigned as tail + 1 and never reused while rows are only appended, so on an
          intact chain the span between two sequences is the number of rows between them. What makes
          it an assumption rather than a fact is that VerifyAsync never checks CONTIGUITY. It does
          now read `Sequence` for more than the range: since the epoch gained two ends the walk
          compares every row's sequence against the bounds of the key answering for it, four
          comparisons in all. What it never does is check that one row's sequence follows the last.
          So somebody holding the keys covering a stretch can delete an interior row from it and
          recompute the links behind it, leaving an INTACT chain with a hole in its numbering -- and
          the window then counts a sequence that has no row.

          (This said the per-row checks are "the link, the payload version, the key identity and the
          hash, and `Sequence` is read only for the range it reports", and named Audit:ChainKey as
          the key that makes the deletion possible. Both were true before the ring; the same file
          corrected the identical singular-key overclaim fifty lines above and left this one.)

          That error is conservative: the window reads larger than the truth, never smaller. It is
          still stated in the printed text, because a number whose unit is wrong under one adversary
          is a number whose unit has to be named.
        */
        if (!coverage.ChainVerified)
        {
            return
            [
                "UNCOVERED WINDOW: not computed -- the anchor chain did not verify.",
                "  A chain with a record missing or mis-linked can claim any coverage at all, so",
                "  arithmetic on it would produce a number with nothing behind it. Run `anchor` to",
                "  see what is wrong with it.",
            ];
        }

        if (coverage.Records == 0)
        {
            return
            [
                "UNCOVERED WINDOW: EVERY row. No anchor has ever been recorded.",
                "  There is nothing for the current tail to be compared against, so a truncation",
                "  would leave no trace at all. Run `anchor`, then `export` the result somewhere",
                "  this machine cannot reach.",
            ];
        }

        if (coverage.DeepestCovered is not { } covered)
        {
            /*
              RECORDS EXIST AND NONE OF THEM COVERS ANYTHING, which is a real state rather than a
              defensive branch: every run against an empty or broken chain writes a GAP MARKER, and a
              marker's coverage columns are null by construction. A deployment that only ever ran
              `anchor` while the table was empty has a populated anchor chain covering nothing.
            */
            return
            [
                $"UNCOVERED WINDOW: EVERY row. {coverage.Records:N0} anchor records exist and none",
                "  of them covers anything -- every one is a gap marker, which asserts coverage of",
                "  nothing by construction. A populated anchor table is not the same as an anchored",
                "  chain, and this is the case where the two look alike.",
            ];
        }

        var tail = highest ?? 0;

        if (covered > tail)
        {
            return
            [
                $"⚠️ UNCOVERED WINDOW: NEGATIVE. The anchors claim coverage through sequence"
                    + $" {covered:N0}, and the chain ends at {tail:N0}.",
                $"  {covered - tail:N0} sequences that an anchor says it saw are not there now. That",
                "  is what a tail truncation looks like when whoever did it left the anchor records",
                "  behind. It is NOT proof on its own -- the anchor key holder can write records",
                "  claiming anything -- but nothing legitimate produces it. Preserve the database and",
                "  escalate before running anything that writes.",
                .. WhatTheWindowCannotSee(),
            ];
        }

        var window = tail - covered;

        return
        [
            window == 0
                ? "UNCOVERED WINDOW: at least 0 rows -- the deepest anchor reaches the current tail."
                : $"UNCOVERED WINDOW: at least {window:N0} rows are outside every anchor.",
            $"  The deepest coverage any record claims is sequence {covered:N0}; the chain ends at"
                + $" {tail:N0}.",
            "  AT LEAST, because rows can be appended while this walk runs. And it is not an upper",
            "  bound on anything: nothing here schedules an anchor, so the window has no ceiling and",
            "  a small number now says nothing about tomorrow. A missing anchor is not evidence.",
            "  Counted in SEQUENCE NUMBERS, which is a row count unless somebody holding",
            "  Audit:ChainKey removed rows and recomputed the links behind them -- the walk checks",
            "  the links, never the contiguity. Against anyone else the two are the same number.",
            "  So if your own COUNT(*) over this range disagrees with this span, that gap is the",
            "  finding, not a fault in this tool: it is the one trace a key-holding interior",
            "  deletion leaves. The verdict above already tells you to compare counts -- this is",
            "  what it means when they differ.",
        ];
    }

    /// <summary>
    /// The two limits the window has that its own arithmetic cannot show, printed with the negative
    /// case because that is the only place an operator might mistake it for a detector.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>IT HEALS.</b> <c>Sequence</c> is assigned as the current tail plus one, so a truncated
    /// table reissues the numbers it lost: cut to 3,000 against an anchor claiming 5,000 and the
    /// window reads −2,000, but write 2,000 further rows and the tail is back at 5,000 and the window
    /// reads a clean zero. The signal is loud only while the table is still shorter than the deepest
    /// claim, which on a busy system is not long.
    /// </para>
    /// <para>
    /// ⚠️ <b>AND IT IS BLIND TO THE THOROUGH VERSION.</b> Delete the anchors that cover past the cut
    /// as well and the deepest claim drops with the tail, so the arithmetic is perfectly consistent
    /// and prints nothing at all. That is
    /// <c>ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit</c>, asserted on
    /// SQL Server, and this number does not change it. What it catches is the version performed by
    /// somebody who did not know the anchor table was there.
    /// </para>
    /// </remarks>
    private static string[] WhatTheWindowCannotSee() =>
    [
        "  Two things this number cannot do, so it is not mistaken for a detector. It HEALS:",
        "  sequences are reissued after a truncation, so writing enough new rows brings the tail",
        "  back past the claim and the window reads zero again. And it is blind to the thorough",
        "  version: delete the covering anchors too and the claim drops with the tail, which is",
        "  ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit.",
    ];

    /// <summary>
    /// Does the whole job and returns what to print and what to exit with. Never throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SEPARATED FROM THE COMMAND so the failure paths can be asserted, and because the exit code
    /// is the part automation reads. Only 0, 1 and 2 are verdicts about the CHAIN; anything that
    /// prevented the walk is 3, "no verdict".
    /// </para>
    /// <para>
    /// <b>THE CATCH IS DELIBERATELY BROAD, and measured rather than guessed.</b> Left uncaught,
    /// System.CommandLine turns any handler exception into exit <b>1</b> -- which in this tool means
    /// CHAIN BROKEN. Measured, all three of these produced it: an unreachable server
    /// (<c>SqlException</c>), a malformed connection string, and no connection string at all
    /// (<c>InvalidOperationException</c>). So a wrong environment variable reported the same thing
    /// as a tampered audit trail, in the one tool whose entire purpose is telling those apart.
    /// Enumerating exception types would have left the next unlisted one colliding again.
    /// </para>
    /// <para>
    /// A genuine bug here also lands on 3, which is the price. It is paid back by printing the
    /// exception TYPE: an operator sees a SqlException and checks their connection string, and a
    /// developer seeing NullReferenceException in that line knows immediately it is ours.
    /// </para>
    /// </remarks>
    public static async Task<(int ExitCode, IReadOnlyList<string> Lines)> RunAsync(
        IServiceProvider services, CancellationToken cancellationToken = default)
    {
        /*
          VALIDATE HERE, NOT AT STARTUP, and the difference is what an unconfigured operator sees.

          This ran in Program.cs before the command line was even parsed, so on a machine where
          nobody had exported a key yet, EVERY invocation died the same way. Measured on a3e31a7:
          `--help`, `--version`, no arguments and a mistyped command all exited 3 with "CANNOT
          VERIFY: this tool is not configured to read the chain" -- no help text, no version, no
          usage message. The command that exists to explain the tool required you to already know
          how to configure it, and the exit-4 usage code added one commit earlier was unreachable
          on exactly the machine where a usage mistake is likeliest.

          Validating at the point of USE keeps the guarantee that mattered -- no row is read with a
          key that was never checked -- and costs nothing else.
        */
        try
        {
            services.GetService<IStartupValidator>()?.Validate();
        }
        catch (OptionsValidationException invalid)
        {
            var lines = new List<string>
            {
                "CANNOT VERIFY: this tool is not configured to read the chain.",
            };
            lines.AddRange(invalid.Failures.Select(failure => $"  {failure}"));
            return (Misconfigured, lines);
        }

        try
        {
            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

            /*
              THE RANGE COMES FROM THE WALK, not from two extra queries, which mattered because the
              count and the range used to be taken at different instants: MIN and MAX were asked
              before walking, so a row committed in between was counted and fell outside the range --
              101 rows verified over a range ending at 100.

              WHAT IT IS GOOD FOR IS NARROWER THAN THIS COMMENT USED TO CLAIM. It said the two
              numbers let an operator spot a chain with gaps. They cannot, on an INTACT verdict: a
              deleted prefix leaves the new first row pointing at a predecessor that is gone, which
              is a break, and Sequence is assigned as tail + 1 with no gaps -- so an intact chain
              always reads 1 to <count>, and the range there only confirms what the count says.

              Where it earns its place is a BROKEN verdict, and that is where it was missing: the
              range then says which stretch was actually walked before the walk stopped, on a table
              whose numbering may start anywhere after a purge. The number to compare against
              yesterday is the COUNT.
            */
            /*
              THE ANCHORS ARE READ BEFORE THE WALK, AND THE ORDER IS THE ERROR DIRECTION.

              The window needs two observations from two tables, so it cannot come from one instant
              the way the sequence range does. What can be chosen is which way a race falls. Read the
              anchors FIRST and an anchor written while the walk runs is missed, so the coverage used
              is older, so the window comes out LARGER than the truth -- the operator is told the gap
              is worse than it is. Read them last and that same anchor shrinks the window, and the
              operator is told the gap is smaller than it is. Only one of those is safe to be wrong
              about, and it is not the second.

              This is the same trap the sequence range fell into on this command and had to be pulled
              out of: MIN and MAX were asked before walking, a row committed in between, and it
              printed 101 rows verified over a range ending at 100. The range was fixed by taking it
              FROM the walk. This cannot be -- different table -- so the ordering carries it instead,
              and the printed line says "at least" rather than a bare number.
            */
            var anchors = scope.ServiceProvider.GetRequiredService<IAuditAnchorChain>();
            var anchorState = await anchors.VerifyChainAsync(context, cancellationToken);

            /*
              BOTH NUMBERS COME OUT OF THE WALK ABOVE, and the first version of this asked the table
              for them afterwards -- MaxAsync and LongCountAsync, two more reads. Three reads are
              three instants: a record added between the verification and the maximum is counted in
              the maximum and was never verified, so the coverage reads deeper than anything the walk
              vouched for and the window comes out SMALLER than the truth. That is the one direction
              this number must never be wrong in, and no ordering of three reads fixes it -- only
              having one.

              The count and the maximum are still separate values rather than one, because "no anchor
              was ever written" and "anchors exist and every one is a gap marker" are different
              things to tell an operator, and a null maximum alone cannot tell them apart.
            */
            var coverage = new AnchorCoverage(
                anchorState.IsIntact, anchorState.DeepestCovered, anchorState.Records);

            var verification = await chain.VerifyAsync(context, cancellationToken);

            return Report(
                verification, verification.LowestSequence, verification.HighestSequence, coverage);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            /*
              INTERRUPTION IS DECIDED BY THE TOKEN, NOT BY THE EXCEPTION TYPE, and the first version
              of this guard got that exactly backwards.

              It caught OperationCanceledException. On SQL Server that is only what Ctrl+C produces
              when the token was ALREADY signalled at the moment the call started -- which is what a
              unit test passing a pre-cancelled token creates. Cancel a walk that is genuinely in
              flight and Microsoft.Data.SqlClient sends an attention, the server aborts the batch,
              and the task completes FAULTED with a SqlException carrying "A severe error occurred on
              the current command." and "Operation cancelled by user."; dotnet/SqlClient#26 has been
              open on this since 2016 and there is no faulted-to-cancelled conversion in
              SqlCommand.Reader.cs. So which shape an interruption arrives in is a timing race, the
              guard covered the shape the test manufactures, and the operator got the other one.

              Keying on the token is what EF itself does -- SqlServerExceptionDetector.IsCancellation
              returns true on IsCancellationRequested regardless of the exception. It accepts one
              trade knowingly: a genuine outage that coincides with a signalled token is reported as
              an interruption. That is the better error to make, because the operator who pressed
              Ctrl+C knows they did.

              NOT ex.CancellationToken == cancellationToken, which looks more precise and is worse:
              SqlClient completes its internal cancellations with TrySetCanceled() and no token, so
              the exception carries default.

              WATCH THIS IF A --timeout IS EVER ADDED. Implemented as a linked CancellationTokenSource,
              the timeout leg would fire while this token stayed false, and the guard would go quiet
              again. The whole path carries ONE token today, verified in source.
            */
            return (Interrupted, new[]
            {
                "INTERRUPTED: the walk was stopped before it reached a verdict.",
                "  This says nothing about the store or the key. The interruption is recognised by",
                "  the cancellation token, not by what failed, so a store that died while the token",
                "  was already signalled arrives here too. If you stopped it because it seemed to",
                "  hang, the hang is the thing to look at. Otherwise run it again.",
            });
        }
        catch (AuditKeyRingException ring)
        {
            // BEFORE the generic handler, which would print "the audit store could not be read" --
            // a sentence about the table, for a problem in the configuration, on a run that never
            // opened the table. That mislabelling arrived with the ring guards and is fixed here
            // rather than apologised for in the runbook.
            return RingNotConfigured(ring);
        }
        catch (Exception failure)
        {
            /*
              THE FIRST DATABASE EXCEPTION IN THE CHAIN, which is neither the outermost nor the
              innermost, and all three of the measured cases explain why.

              Unreachable server: the outer IS a SqlException with the useful sentence, while the
              innermost is a Win32Exception saying only "The wait operation timed out". Taking the
              base gave the operator the worse one.

              Database that does not exist: the outer is EF's own wrapper -- "An exception has been
              raised that is likely due to a transient failure. Consider enabling transient error
              resiliency by adding 'EnableRetryOnFailure'" -- which names no cause AND advises
              undoing the deliberate decision in this tool's composition root. EF only raises it
              when retries are off, so turning them off to make the walk stream is what put it
              there. The SqlException underneath says "Cannot open database ... The login failed",
              which is the whole answer.

              Preferring the DbException picks the right one in both, and cannot pick the
              Win32Exception, which is not one.
            */
            var cause = Unwrap(failure);

            return (Misconfigured, new[]
            {
                "CANNOT VERIFY: the audit store could not be read, so there is no verdict.",
                $"  {cause.GetType().Name}: {cause.Message}",
                "  This is NOT a statement about the chain -- but do not assume it is yours to",
                "  fix. A wrong connection string and an AuditEvents that is no longer there exit",
                "  the same way, and a table that has vanished from a database where it belongs is",
                "  the most complete tamper anyone holding write access can manage. Check the",
                "  connection string and the key FIRST. If they are right, preserve the database",
                "  and escalate: re-running migrations recreates the table and erases the evidence.",
            });
        }
    }

    /// <summary>
    /// The first <see cref="DbException"/> in the chain, or the outermost exception if there is none.
    /// </summary>
    private static Exception Unwrap(Exception failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is DbException)
            {
                return current;
            }
        }

        return failure;
    }

    /// <summary>
    /// Combines what the command-line framework returned with what the handler found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE REGRESSION THIS EXISTS TO PIN WAS HERE, not in the constants. System.CommandLine's
    /// default pipeline reports every parse failure as <b>1</b>, and 1 is this tool's word for
    /// CHAIN BROKEN, so returning it unchanged made "no arguments at all" report a tampered audit
    /// trail. The first guard written for it asserted that five compile-time constants were
    /// pairwise distinct -- which they always were, and which no edit to this translation could
    /// ever change. It read like protection and was decoration.
    /// </para>
    /// <para>
    /// The framework's pipeline only ever emits 0 or 1, so any non-zero is a usage or framework
    /// failure and becomes <see cref="UsageError"/>. The handler's own verdict is passed separately
    /// and is used only when the command actually ran.
    /// </para>
    /// </remarks>
    public static int CombineExitCodes(int fromParser, int fromHandler) =>
        fromParser != 0 ? UsageError : fromHandler;

    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "verify",
            "Walk the audit chain and report whether every row still hashes and links.");

        command.SetHandler(async (InvocationContext invocation) =>
        {
            /*
              THE REAL CANCELLATION TOKEN, not default. System.CommandLine's CancelOnProcessTermination
              middleware only engages for a handler that ASKS for the token; passing default left
              Ctrl+C during a long walk unprotected. Asking for it means an interrupted verification
              unwinds through the catch in RunAsync and reports "no verdict", which is what it is.
            */
            var (exitCode, lines) = await RunAsync(services, invocation.GetCancellationToken());

            /*
              THE VERDICT IS RECORDED BEFORE IT IS PRINTED. Writing to a closed stdout -- piping this
              into `head -1`, say -- throws, and an exception escaping the handler is turned into
              exit 1 by the framework, which in this tool means CHAIN BROKEN. Assigning first means a
              broken pipe costs the operator the text and not the answer.
            */
            Environment.ExitCode = exitCode;

            try
            {
                foreach (var line in lines)
                {
                    Console.WriteLine(line);
                }
            }
            catch (IOException)
            {
                // Nothing is reading. The exit code already carries the verdict.
            }
        });

        return command;
    }
}
