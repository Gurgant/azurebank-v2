using System.Text.RegularExpressions;
using AzureBank.AuditVerifier.Commands;
using AzureBank.Infrastructure.Data;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using AzureBank.Shared.Options;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// What the operator-runnable verifier SAYS, and what a script reads from its exit code.
/// </summary>
/// <remarks>
/// The mapping from a verification result to a verdict is the whole tool; everything around it is
/// plumbing. It is tested here rather than through the console because the outcomes that matter
/// most -- an empty table, and a break at the first row -- are the ones nobody would think to
/// reproduce by hand during an incident.
/// </remarks>
public class AuditVerifierReportTests
{
    [Fact]
    public async Task AnUnreachableDatabase_IsNoVerdict_AndMustNotLookLikeABrokenChain()
    {
        /*
          THE DANGEROUS COLLISION, MEASURED BEFORE IT WAS FIXED. System.CommandLine turns any
          exception escaping a handler into exit 1 -- which in this tool means CHAIN BROKEN. So an
          unreachable server, a malformed connection string and a missing one all reported the same
          code as a tampered audit trail, in the one tool whose whole purpose is telling those apart.
          An automated check would have paged somebody about a possible attack over a typo in an
          environment variable.

          localhost,1 with a two-second connect timeout: refused fast, and nothing about the failure
          depends on which machine runs the test.
        */
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AzureBankDbContext>(o => o.UseSqlServer(
            "Server=localhost,1;Database=Nope;User Id=u;Password=p;TrustServerCertificate=True;Connect Timeout=2"));
        services.AddSingleton<IAuditChain>(new AuditChain(
            Options.Create(new AuditOptions { ChainKey = new string('k', 32) }),
            NullLogger<AuditChain>.Instance));

        using var provider = services.BuildServiceProvider();

        var (exitCode, lines) = await VerifyCommand.RunAsync(provider);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(
            VerifyCommand.Misconfigured,
            "a database this tool cannot reach is a fact about the invocation, not about the bank");
        exitCode.Should().NotBe(
            VerifyCommand.Broken,
            "this is THE collision that mattered: automation reading 1 would treat an unreachable "
            + "server as a tampered audit trail");
        text.Should().Contain("CANNOT VERIFY");
        text.Should().Contain(
            "NOT a statement about the chain",
            "the operator has to be told what the result does not mean, not only what it is");
    }

    [Fact]
    public void AParseFailureIsNeverPassedThroughAsAVERDICT()
    {
        /*
          THE ASSERTION THAT WAS MISSING, and the one before it looked like protection.

          That guard read five compile-time constants and asserted they were pairwise distinct.
          They always were, and no edit could make them otherwise -- the reused value was
          System.CommandLine's 1, which lives outside this assembly and arrives through the
          translation below. Reverting that translation restored "no arguments = tampered audit
          trail" with the whole suite green, which is the definition of a test that cannot see the
          regression it names.
        */
        VerifyCommand.CombineExitCodes(1, VerifyCommand.Intact).Should().Be(
            VerifyCommand.UsageError,
            "the framework reports EVERY parse failure as 1, and passing it through makes a typo "
            + "indistinguishable from a tampered chain -- which is what it did");

        VerifyCommand.CombineExitCodes(1, VerifyCommand.Intact).Should().NotBe(
            VerifyCommand.Broken, "that is the collision, stated as itself");

        VerifyCommand.CombineExitCodes(0, VerifyCommand.Broken).Should().Be(
            VerifyCommand.Broken, "when the command DID run, its verdict is the answer");
        VerifyCommand.CombineExitCodes(0, VerifyCommand.Intact).Should().Be(VerifyCommand.Intact);
        VerifyCommand.CombineExitCodes(0, VerifyCommand.Misconfigured).Should().Be(
            VerifyCommand.Misconfigured, "and a no-verdict must not be flattened into success");

        /*
          ALL SEVEN, and it listed five. Interrupted and NotRecorded were both absent -- the second
          because it arrived with the anchor mode in a different file, which is exactly how a list
          that lives in three places loses an entry. A uniqueness assertion over a subset cannot
          catch the collision it exists to catch.
        */
        new[]
        {
            VerifyCommand.Intact, VerifyCommand.Broken, VerifyCommand.NothingToVerify,
            VerifyCommand.Misconfigured, VerifyCommand.UsageError, VerifyCommand.Interrupted,
            AnchorCommand.NotRecorded,
        }.Should().OnlyHaveUniqueItems("two meanings on one number is a signal nothing can read");
    }

    [Fact]
    public async Task AnInterruptedWalkSaysSo_RatherThanBlamingTheStore()
    {
        /*
          Ctrl+C cancels the token, VerifyAsync rethrows, and before this the message told the
          operator to check the connection string and the key -- neither of which was the problem,
          because they stopped it themselves. The code stays 3: a walk that was interrupted checked
          part of the chain and proved nothing about the rest, which is what "no verdict" means.
        */
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AzureBankDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton<IAuditChain>(new AuditChain(
            Options.Create(new AuditOptions { ChainKey = new string('k', 32) }),
            NullLogger<AuditChain>.Instance));

        using var provider = services.BuildServiceProvider();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var (exitCode, lines) = await VerifyCommand.RunAsync(provider, cancelled.Token);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(
            VerifyCommand.Interrupted,
            "an interruption is its own outcome: e2fsck documents 32 for 'canceled by user request' "
            + "separately from 8 'operational error', and AIDE 25 separately from its IO and database "
            + "codes. Folding it into 3 had already misfired here -- the runbook tells an operator to "
            + "alert on 3 with a triage list of environment failures, none of which applies to "
            + "somebody pressing Ctrl+C");
        exitCode.Should().NotBe(
            VerifyCommand.Misconfigured, "which is what it used to be, and what the runbook glosses "
            + "as 'the store could not be read'");
        text.Should().Contain("INTERRUPTED", "the operator has to know it was them");
        text.Should().NotContain(
            "could not be read",
            "the store was fine -- reporting a store failure sends them to check a connection "
            + "string that was never the problem");
    }

    [Fact]
    public async Task AnInterruptionIsRecognisedWHATEVERShapeItArrivesIn()
    {
        /*
          THE CASE THE FIRST GUARD COULD NOT SEE, and the reason it could not.

          That guard caught OperationCanceledException. On SQL Server that is only what Ctrl+C
          produces when the token was ALREADY signalled when the call started -- which is precisely
          what a test passing a pre-cancelled token manufactures. Cancel a walk that is genuinely in
          flight and SqlClient sends an attention, the server aborts the batch, and the task faults
          with a SqlException instead (dotnet/SqlClient#26, open since 2016). So the guard covered
          the shape the test creates and the operator got the other one.

          A SqlException cannot be constructed here -- it has no public constructor -- so this pins
          the property that matters instead: the classification must not depend on the exception
          TYPE at all. A chain that throws something entirely unrelated, with the token signalled,
          must still be reported as an interruption.
        */
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AzureBankDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton<IAuditChain>(new ThrowingChain(new InvalidOperationException("not an OCE")));

        using var provider = services.BuildServiceProvider();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var (exitCode, lines) = await VerifyCommand.RunAsync(provider, cancelled.Token);

        exitCode.Should().Be(
            VerifyCommand.Interrupted,
            "the token is signalled, so this is an interruption whatever the exception's type -- "
            + "which is what EF itself does: SqlServerExceptionDetector.IsCancellation keys on "
            + "IsCancellationRequested regardless of the exception");
        string.Join(" ", lines).Should().NotContain(
            "could not be read",
            "blaming the store is the wrong message for somebody who stopped it themselves");
    }

    [Fact]
    public async Task AnInterruptionDoesNotCERTIFYTheStore()
    {
        /*
          IT USED TO SAY "Nothing is wrong with the store or the key", which is more than this
          verdict can see. The branch is chosen by cancellationToken.IsCancellationRequested and not
          by what threw -- the code's own comment concedes the trade -- so a store that failed while
          the token was already signalled lands here wearing the same words. And the likeliest way
          to signal that token is an operator stopping a walk that appeared to hang, which is
          precisely the case where something IS wrong with the store.
        */
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AzureBankDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton<IAuditChain>(new ThrowingChain(new InvalidOperationException("store died")));

        using var provider = services.BuildServiceProvider();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var (exitCode, lines) = await VerifyCommand.RunAsync(provider, cancelled.Token);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(VerifyCommand.Interrupted);
        text.Should().NotContain(
            "Nothing is wrong",
            "this verdict cannot establish that, and saying it retires the one hypothesis worth "
            + "keeping when a walk was stopped for appearing to hang");
        text.Should().Contain(
            "hang",
            "and it has to name the case, or an operator reads 'interrupted' and closes it");
    }

    [Fact]
    public async Task AStoreThatCannotBeReadDoesNotWriteOffAVanishedTable()
    {
        /*
          MEASURED ON SQL SERVER: rename AuditEvents away and the tool exits 3 with
          "SqlException: Invalid object name 'AuditEvents'." -- and used to close with "Check the
          connection string and the key before reading anything into it." A table that has vanished
          from a database where it belongs is the most complete tamper available to anyone holding
          write access, and it was being handed over as an environment problem. Worse, the remedy
          the runbook's step 6 offers for a missing table is to re-run migrations, which recreates
          it and destroys the evidence that it was ever gone.
        */
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AzureBankDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton<IAuditChain>(
            new ThrowingChain(new InvalidOperationException("Invalid object name 'AuditEvents'.")));

        using var provider = services.BuildServiceProvider();

        var (exitCode, lines) = await VerifyCommand.RunAsync(provider, CancellationToken.None);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(VerifyCommand.Misconfigured);
        text.Should().Contain(
            "tamper",
            "a missing audit table has to be named as one of the things this exit code covers, or "
            + "the operator triages the worst case as a typo");
        text.Should().Contain(
            "migrations",
            "and the remedy that would erase the evidence has to be called out where they are "
            + "standing, not left in a runbook step they may never reach");
    }

    /// <summary>An <see cref="IAuditChain"/> that fails the way a cancelled SQL walk does.</summary>
    private sealed class ThrowingChain(Exception failure) : IAuditChain
    {
        public Task ApplyAsync(DbContext context, CancellationToken cancellationToken = default) =>
            throw failure;

        public void Apply(DbContext context) => throw failure;

        public Task<AuditChainVerification> VerifyAsync(
            DbContext context, CancellationToken cancellationToken = default) => throw failure;
    }

    [Fact]
    public void AnEmptyTableIsNOTReportedAsIntact()
    {
        /*
          THE TRAP THIS PROJECT HAS ALREADY FALLEN INTO ONCE. VerifyAsync reports IsIntact for zero
          rows, which is true and useless: a chain of nothing links perfectly. A table truncated to
          nothing therefore looks exactly like a freshly migrated one, and printing "intact" over it
          would tell an operator the opposite of what happened.
        */
        var (exitCode, lines) = VerifyCommand.Report(new AuditChainVerification(0, null, null), null, null);

        exitCode.Should().Be(
            VerifyCommand.NothingToVerify,
            "a script that treated this as success would pass a check it never performed");
        exitCode.Should().NotBe(VerifyCommand.Intact);
        string.Join(" ", lines).Should().NotContain(
            "INTACT", "the word itself is the thing that would mislead");
    }

    [Fact]
    public void ABreakAtTheFirstRow_PointsAtTheKEYBeforeAnAttacker()
    {
        /*
          A WRONG key is well-formed, so the options validation passes it, and it mismatches from
          row one -- every time, because the first hash it recomputes is already different. A real
          tamper breaks where it happened instead. Position is the only tell available, and without
          it an operator opens an incident about an attacker who does not exist.
        */
        /*
          THE FIXTURE STARTS AT SEQUENCE 1, and the version of this test that used 5,001 was pinning
          a state the system cannot reach. VerifyAsync checks the LINK before the hash, so the only
          row that can reach the hash check first is one recording no predecessor, and Link writes
          that only into an empty table -- where the row it writes is sequence 1. A chain whose head
          was removed breaks on the LINK instead and never reaches the hash check: measured on SQL
          Server, a wrong key against a decapitated chain prints output identical to the right key.

          The old fixture therefore proved the hint fired in a case that cannot occur, and the gate
          it justified let the hint fire in one that can -- see the sibling test below.
        */
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(
                0, 1, "Row ... does not match its own hash", 1, 5,
                AuditChainBreakKind.HashMismatch),
            1, 5);

        exitCode.Should().Be(VerifyCommand.Broken);

        /*
          ⚠️ "Audit:ChainKey" IS A SUBSTRING OF "Audit:FoundingChainKey", and after the key ring this
          branch prints the second. So the assertion below kept passing while the sentence it was
          written for had been replaced -- green, and about a different key. It now asserts the key
          this branch actually names, which is the one applied to a row recording no identity.
        */
        var text = string.Join(" ", lines);
        text.Should().Contain(
            "Audit:FoundingChainKey",
            "a row that records no key identity is checked under the FOUNDING key, so that is the "
            + "one to confirm — Audit:ChainKey is it only while nothing has been retired");
        text.Should().Contain(
            "Confirm the key before escalating",
            "and the hint has to survive as one phrase: a re-wrap that split it would leave the "
            + "sibling test's NotContain passing over a sentence that is still printed");
    }

    [Fact]
    public void AnUncheckedRow_ExitsBroken_AndNamesBothIdentities()
    {
        /*
          THE MUZZLE TEST, AT THE LAYER AUTOMATION ACTUALLY READS. A row this verification cannot
          check is not a row it proved good, and the exit code has to say so: anything that scripts
          the verifier branches on the integer, not on the prose. Mapping this to Misconfigured or
          NothingToVerify would let somebody hide a tampered row by overwriting its key identity --
          the verdict would soften from evidence to housekeeping.

          FALSIFIED by mapping the kind to any other exit code, or by gating the reading on
          Verified == 0: a table holding both renderings verifies its legacy prefix first, so a
          wrong key surfaces here with Verified > 0 and a zero-gate would print nothing at all.
        */
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(
                7, 8, "Row ... was written under key id ...", 1, 12,
                AuditChainBreakKind.UnknownScheme, "v3", "ffffffffffffffff", "b78e425e698034a4"),
            1, 12);

        exitCode.Should().Be(
            VerifyCommand.Broken,
            "an unchecked row is a break -- silence is not a verdict this tool is allowed to reach");

        var text = string.Join(" ", lines);
        text.Should().Contain("ffffffffffffffff", "the operator needs the id the row carried");
        text.Should().Contain("b78e425e698034a4", "and the id the key they are holding derives");
        text.Should().Contain("NOT checked", "the distinction from 'checked and good' is the point");
    }

    [Fact]
    public void AHashMismatchAtSequence1_OnARowThatNamesItsKey_ConfirmsTheKeyInsteadOfBlamingIt()
    {
        /*
          THE EXONERATION, RE-ARMED FOR THE NEW ROUTE. The sibling above earns the "confirm the key"
          hint because a row recording no key identity cannot rule the key out. A row that DOES name
          its key has already had it confirmed -- the scheme check refuses a mismatched identity
          before any hash is recomputed -- so printing the old hint here would send an operator to
          re-check a key the tool just proved right, while a genuine write went unescalated.

          FALSIFIED by removing the version gate on the hint: this reddens immediately.
        */
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(
                0, 1, "Row ... does not match its own hash", 1, 5,
                AuditChainBreakKind.HashMismatch, "v3", "b78e425e698034a4", "b78e425e698034a4"),
            1, 5);

        exitCode.Should().Be(VerifyCommand.Broken);

        var text = string.Join(" ", lines);
        text.Should().NotContain(
            "Confirm the key before escalating",
            "the key is already confirmed for this row, so sending the operator back to it is wrong");
        text.Should().Contain("WRITE", "which makes this an escalation, not a configuration check");
    }

    [Fact]
    public void AHashMismatchABOVESequence1_IsAWriteAndMustNotBeBlamedOnTheKey()
    {
        /*
          THE EXONERATION THIS TOOL SHIPPED WITH, until it was measured. Deleting the oldest rows and
          then clearing the survivor's PreviousHash is the cheapest way to hide a deleted prefix: the
          link check passes, because null is what the start of a chain looks like, and the hash check
          fails, because the stored hash still covers the predecessor that was cleared. That is a
          HashMismatch with nothing verified, above sequence 1.

          The gate keyed on the count alone, so the tool answered "usually means the wrong
          Audit:ChainKey ... Confirm the key before escalating" -- with the CORRECT key in use, and
          while printing "Sequences read: 2 to 2" one line above the words "from row one". Measured
          on SQL Server: rows written through the real stack, one DELETE, one UPDATE, correct key.

          A wrong key cannot produce this: it would have to be sequence 1. So naming the key here is
          never right, and it is wrong in the only direction that matters.
        */
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(
                0, 5_001, "Row ... does not match its own hash", 5_001, 5_005,
                AuditChainBreakKind.HashMismatch),
            5_001, 5_005);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(VerifyCommand.Broken);
        text.Should().NotContain(
            "Audit:ChainKey",
            "a wrong key breaks at sequence 1 or not at all, so pointing at it here exonerates "
            + "whoever wrote that row -- the one direction this tool must never get wrong");
        text.Should().Contain(
            "WRITTEN",
            "and it has to say what the state actually means, or the operator is left with a "
            + "mismatch and no reading of it");
    }

    [Theory]
    [InlineData(AuditChainBreakKind.LinkBroken)]
    [InlineData(AuditChainBreakKind.Unreadable)]
    public void OnlyAHashMismatchEverBlamesTheKey(AuditChainBreakKind kind)
    {
        /*
          A WRONG KEY CANNOT PRODUCE EITHER OF THESE, and the hint used to fire on both because it
          keyed on "nothing verified" alone. A key cannot change what a row records as its
          predecessor, and it cannot make a row unreadable -- so on a deleted prefix, and on a row
          whose stored Outcome is not a member of the enum, the tool was telling an operator to go
          and check a key that was never the problem. Both measured before this gate existed.
        */
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(0, 5_001, "...", 5_001, 5_001, kind), 5_001, 5_001);

        exitCode.Should().Be(VerifyCommand.Broken);
        string.Join(" ", lines).Should().NotContain(
            "Audit:ChainKey",
            "a wrong key is well-formed and mismatches a HASH; it cannot break a link and it cannot "
            + "make a row unreadable, so naming it here sends the operator away from the real cause");
    }

    [Fact]
    public void ALinkBreakAtSequence1_IsAWRITE_NotARemoval()
    {
        /*
          THE FIRST ROW OF THE CHAIN RECORDS NO PREDECESSOR -- Link writes null there, because there
          was no tail. So a link break AT sequence 1 cannot mean rows were removed from the head:
          the row is still the head. Something wrote a predecessor onto it.

          Measured on SQL Server: UPDATE AuditEvents SET PreviousHash = REPLICATE('a',64) WHERE
          Sequence = 1 gives "CHAIN BROKEN at sequence 1 ... Sequences read: 1 to 1" with all three
          rows still present. The runbook read this verdict as "the OLDEST rows are gone", which is
          the opposite of what happened, and it is what the operator would have acted on.
        */
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(
                0, 1, "Row ... expected to follow '(start of chain)' but records ...", 1, 1,
                AuditChainBreakKind.LinkBroken),
            1, 1);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(VerifyCommand.Broken);
        text.Should().Contain(
            "NOTHING was removed",
            "the head is still here, so telling the operator to go and find the archival job that "
            + "removed it sends them after something that did not happen");
        text.Should().NotContain(
            "rows BELOW",
            "there are no rows below sequence 1, and saying otherwise invents a deletion");
    }

    [Fact]
    public void ALinkBreakABOVESequence1_MeansTheRowsBeneathAreGone()
    {
        // The other half. Same kind, same count, different position, opposite reading -- which is
        // the whole point of splitting on it.
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(
                0, 5_001, "Row ... expected to follow '(start of chain)' but records ...",
                5_001, 5_005, AuditChainBreakKind.LinkBroken),
            5_001, 5_005);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(VerifyCommand.Broken);
        text.Should().Contain(
            "rows BELOW",
            "a chain that starts above 1 is missing what came before it, and that is the fact the "
            + "operator has to take away");
        text.Should().NotContain(
            "NOTHING was removed",
            "which would be the exact inversion of the state");
    }

    [Fact]
    public void ABreakInTheMIDDLE_DoesNotBlameTheKey()
    {
        // The negative control. If the hint appeared on every break it would be noise, and the one
        // case where it means something would be indistinguishable from the ones where it does not.
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(
                4_312, 4_313, "Row ... expected to follow ...", 1, 4_313,
                AuditChainBreakKind.LinkBroken),
            1, 9_000);

        exitCode.Should().Be(VerifyCommand.Broken);
        string.Join(" ", lines).Should().NotContain(
            "Audit:ChainKey",
            "a break deep in the table is where a tamper would show, and misdirecting to the key "
            + "would send the operator away from it");
        string.Join(" ", lines).Should().Contain("4,313", "the operator needs the position");
        string.Join(" ", lines).Should().Contain("4,312", "and how much verified before it");
    }

    [Fact]
    public void TheUnknownSchemeBlockEnumeratesEVERYWayToReachIt_NotTheThreeItUsedTo()
    {
        /*
          THE PROSE WAS REWRITTEN AND NOTHING ASSERTED IT. The verdict block for UnknownScheme listed
          THREE readings, led by "you hold a different key than the one that wrote this row". The key
          ring added two causes in which the verification DOES hold the row's key -- above a retired
          key's epoch, and above the founding key's -- and the epoch's lower bound added a third.
          For those, the old first reading was false while sitting at the top of the list an operator
          reads to decide what to check.

          The rewrite that fixed it shipped with no test at all: `git log` over
          backend/tests/AzureBank.Tests/Unit/Tools/ shows the commit that changed this text touched
          nothing here, while the commit BEFORE it had paired its change with assertions. This is
          that pairing put back.

          It asserts the SHAPE rather than the sentences: how many readings are offered, and that the
          list does not lead with a claim that is false for most of them. Pinning the wording would
          make every future rewrite red for no reason, which is how a test stops being read.
        */
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(
                0, 1, "Row ... was written under key id 'ffffffffffffffff' and no key ...", 1, 5,
                AuditChainBreakKind.UnknownScheme, "v3", "ffffffffffffffff", "b78e425e698034a4"),
            1, 5);

        exitCode.Should().Be(VerifyCommand.Broken);

        /*
          ⚠️ THE NUMBER COMES FROM AuditChain, NOT FROM THE BLOCK, and the first version of this test
          got that backwards: it counted the bullets and asserted the count it found, so it agreed
          with whatever the block happened to say. It asserted FIVE while the code already had six --
          the epoch's lower bound had landed a commit earlier and the block was never updated.

          The six, each a distinct return or switch arm in AuditChain.VerifyAsync:
            1. the payload version cannot be rendered by this build;
            2. a 'v2' row carries a key id, which that version has nowhere to keep;
            3. no key in the ring has the row's id;
            4. the ring holds the key, the row is ABOVE its boundary;
            5. the row records no id and is above Audit:FoundingChainKey's boundary;
            6. the ring holds the key, the row is BELOW the epoch it opens.

          An overwritten column is NOT a seventh: it is how several of the above come about, since
          PayloadVersion and KeyId are inside the hashed payload. Listing it as a peer is what made
          the old block read as five when it covered four.
        */
        var readings = lines.Count(line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal));
        readings.Should().Be(
            7,
            "a list that offers fewer causes than the walk can return sends an operator looking for "
            + "one it does not name, and each of the six takes a different action");

        var text = string.Join(" ", lines);
        text.Should().NotContain(
            "you hold a different key than the one that wrote this row",
            "THE OLD LEADING READING, false for three of the five: on every boundary verdict the "
            + "ring HOLDS the key and refuses the row anyway. Leading with it sent an operator to "
            + "compare two ids that are not supposed to match");
        text.Should().Contain(
            "not by itself the problem",
            "and the block has to say so where it prints the two ids, or an operator reads the "
            + "mismatch as the finding");
        text.Should().Contain(
            "BELOW the epoch it opens",
            "the lower bound is a cause the walk returns, so the list has to offer it — it was "
            + "missing for two commits while this test asserted the block's own count back at it");
    }

    [Fact]
    public void AnIntactChainReportsTheCOUNT_AndDoesNotOverclaim()
    {
        /*
          THE COUNT AND THE RANGE MUST BE TELLABLE APART, which the first version of this test could
          not do: it passed 40,006 as BOTH the verified count and the highest sequence, so asserting
          the text contained "40,006" was satisfied by the range line alone. Deleting the count from
          the report entirely would have left this green -- the exact regression its own rationale
          says it exists to catch.

          Distinct numbers only so the two lines can be told apart. The earlier version of this
          comment claimed they let an operator spot a chain with GAPS -- which an INTACT verdict
          cannot have: a deleted prefix breaks the link, and Sequence is assigned as tail + 1 with
          no holes, so an intact chain always reads 1 to <count>. The fixture is a state the walk
          cannot produce; it is used here because this test is about the SHAPE of the report, and
          the range's real value is on a broken verdict, which the sibling test covers.
        */
        var (exitCode, lines) = VerifyCommand.Report(
            new AuditChainVerification(40_006, null, null), 7, 91_234);
        var text = string.Join(" ", lines);

        exitCode.Should().Be(VerifyCommand.Intact);
        lines.Should().Contain(
            line => line.Contains("40,006") && !line.Contains("Sequence range"),
            "the COUNT has to appear on its own line -- asserting only that the text contains it "
            + "lets the range line satisfy the assertion while the count is gone");
        lines.Should().Contain(
            line => line.Contains("Sequence range") && line.Contains("7") && line.Contains("91,234"),
            "and the range has to carry both ends, or it cannot be compared with anything");
        text.Should().Contain(
            "NOT prove",
            "tail truncation is undetectable by construction, and the tool must say so where the "
            + "operator reads the good news rather than only in the ADR");
        text.Should().Contain(
            "Audit:ChainKey",
            "THE OTHER HALF OF THE OVERCLAIM, which this test used to let through while carrying "
            + "\"DoesNotOverclaim\" in its name. The hash is an HMAC, so an intact verdict says "
            + "nothing at all about an attacker who took the key along with the database. ADR-0044 "
            + "D2 states the narrow claim -- tampering by someone who holds the database but NOT "
            + "the key, except at the end of the table -- and records that the runbook had already "
            + "repeated the too-strong version once after that section withdrew it. This assertion "
            + "is what stops the tool from being the third place it comes back");
        /*
          ASSERT THE CLAIM, NOT A TOKEN, AND NOT THE LINE BREAKS EITHER. `Contain("RING")` passed on
          a single word, so restoring the old current-key-only sentence while any other line said
          "RING" would have kept this green. Asserting the whole phrase against the raw text is the
          opposite mistake: the sentence is emitted as separate console lines, so the phrase spans a
          break and a literal match pins where that break falls -- the review's own suggested literal,
          "a key in the RING", does not appear in the output for exactly that reason. Collapsing the
          whitespace first asserts what the sentence SAYS and lets it be re-wrapped freely.
        */
        var claim = Regex.Replace(text, @"\s+", " ");
        claim.Should().Contain(
            "does not hold the key whose EPOCH that row falls in",
            "the narrowed claim has to be the one printed, not merely a word from it — and it "
            + "narrowed twice: first from Audit:ChainKey to the ring, then from the ring to the one "
            + "key whose epoch contains the row");
        claim.Should().NotContain(
            "a retired key for the rows at or below its boundary",
            "the pre-lower-bound description of what a retired key answers for, printed on every "
            + "intact verdict until the epoch gained a start");
        claim.Should().NotContain(
            "altered by anyone who does not hold Audit:ChainKey",
            "and the sentence it replaced has to be absent, or a partial regression restores the "
            + "overclaim while every assertion above still passes");
        /*
          THE SUBSUMED ASSERTION IS GONE, and its reason is worth keeping for one line: it asserted
          the single token "RING" underneath an assertion that already required the whole phrase, so
          it could never fail on its own. It then failed for the opposite reason — the claim narrowed
          again, from "a key in the ring" to "the key whose epoch that row falls in", and the token
          disappeared. An assertion that only ever fails when something ELSE has already failed is
          noise until the day it misleads.
        */
    }
}
