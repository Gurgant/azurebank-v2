using System.CommandLine;
using System.CommandLine.Invocation;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureBank.AuditVerifier.Commands;

/// <summary>
/// Walks the audit chain once and records what it found, as a record chained to the one before it
/// and authenticated under a key the database does not hold.
/// </summary>
/// <remarks>
/// <para>
/// A MODE OF THIS TOOL, NOT A SCHEDULED JOB, and that is a decision rather than an omission. Nothing
/// in this deployment runs between sessions, so there is no process for a cadence to live in, and
/// "anchor when somebody remembers" is not a schedule. Anchoring reuses the ONE walk <c>verify</c>
/// already performs: a second <c>COUNT(*)</c> plus <c>MAX(Sequence)</c> would be two instants, and
/// two instants eventually anchor a state that never existed.
/// </para>
/// <para>
/// ⚠️ WHAT THIS DOES NOT DO, said here because the table's existence invites the opposite reading.
/// It detects no truncation. Truncate the audit rows above some sequence, then delete every anchor
/// covering past it, and both chains verify perfectly — each links backwards only. What a record
/// buys is that DELETING an INTERIOR anchor is loud — a suffix removal is not — while MINTING one
/// needs <c>Audit:AnchorKey</c>. The
/// evidence is the pair of numbers an operator wrote down somewhere this machine cannot reach.
/// </para>
/// </remarks>
public static class AnchorCommand
{
    /// <summary>
    /// The walk produced a verdict, but no record could be written from it.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM EVERY OTHER CODE ON PURPOSE. Folding it into <see cref="VerifyCommand.Intact"/>
    /// would report success for a run that wrote nothing; folding it into
    /// <see cref="VerifyCommand.Misconfigured"/> would claim there was no verdict when there was
    /// one. Automation branches on the integer, so the distinction has to exist there and not only
    /// in the prose.
    /// </remarks>
    public const int NotRecorded = 6;

    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "anchor",
            "Walk the chain once and record what it looked like, chained and authenticated.");

        command.SetHandler(async (InvocationContext invocation) =>
        {
            var (exitCode, lines) = await RunAsync(services, invocation.GetCancellationToken());

            // Recorded before printed, for the reason verify records: writing to a closed stdout
            // throws, and an escaping exception becomes exit 1 -- which in this tool means BROKEN.
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
                // A broken pipe costs the operator the text, never the answer.
            }
        });

        return command;
    }

    internal static async Task<(int ExitCode, string[] Lines)> RunAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        /*
          THIS GUARD USED TO READ options.Value AND WAS THEREFORE UNREACHABLE. It validated at the
          point of use, "exactly where verify validates its own key" -- and then Audit:AnchorKey was
          added to the options Validate() in ServiceCollectionExtensions, checking the identical
          predicate. Reading .Value is what triggers that validation, so every input this guard could
          have rejected threw OptionsValidationException on the line above the `if`. Nothing caught
          it.

          ⚠️ MEASURED 2026-08-28 on the shipped build, by running the recovery procedure in
          docs/runbooks/audit-chain-unavailable.md. With Audit__AnchorKey unset, and again with a
          10-character one, `anchor` printed an unhandled OptionsValidationException stack trace and
          exited 4 -- the code this tool defines as "the command line was wrong", on a command line
          that was right. The sentence below was printed zero times. `export` did the same. Only
          `verify` answered 3 with prose, because only VerifyCommand ran the validator inside a try.

          The comment above the extensions guard says the mirror "predicted its own failure and then
          suffered it". It did so twice: adding the earlier guard silently killed the later one, and
          the walk that found it was not a test but an operator's page being read out loud.

          So validate the way verify does -- explicitly, before anything resolves options, catching
          the failure rather than letting it escape -- and keep the sentence, which is still the one
          worth printing.
        */
        try
        {
            services.GetService<IStartupValidator>()?.Validate();
        }
        catch (OptionsValidationException)
        {
            return (VerifyCommand.Misconfigured, new[]
            {
                "NO VERDICT: Audit:AnchorKey is not configured, so nothing can be recorded.",
                "  A record nobody can authenticate is a row anybody holding the database can write,",
                "  which is the one thing this table exists not to be. Set it the way the other five",
                "  secrets are set -- user-secrets in development, environment or vault elsewhere.",
            });
        }

        using var scope = services.CreateScope();

        /*
          AND THE POINT-OF-USE GUARD STAYS, BESIDE THE VALIDATOR RATHER THAN INSTEAD OF IT. Replacing
          it was tried and AMissingAnchorKey_IsRefusedBeforeAnythingIsRead reddened at "expected 3,
          found 2": on a provider composed without options validation -- which is what every existing
          test builds, and the reason this defect stayed invisible -- GetService<IStartupValidator>()
          returns null, the catch above can never fire, and the command walked an empty chain and
          reported NothingToVerify instead of refusing. Two guards for one predicate is not redundancy
          here; they answer for two different compositions, and the tool must refuse under both.
        */
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AuditOptions>>();
        if (string.IsNullOrWhiteSpace(options.Value.AnchorKey) || options.Value.AnchorKey.Length < 32)
        {
            return (VerifyCommand.Misconfigured, new[]
            {
                "NO VERDICT: Audit:AnchorKey is not configured, so nothing can be recorded.",
                "  A record nobody can authenticate is a row anybody holding the database can write,",
                "  which is the one thing this table exists not to be. Set it the way the other five",
                "  secrets are set -- user-secrets in development, environment or vault elsewhere.",
            });
        }

        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();
        var anchors = scope.ServiceProvider.GetRequiredService<IAuditAnchorChain>();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        try
        {
            /*
              THE WHOLE CHAIN, NOT ONLY ITS NEWEST RECORD, and the difference is the whole property.

              Authenticating the tail alone leaves an interior deletion invisible: the survivor still
              verifies under the key, so a run would extend a chain with a hole in it and the new
              record's link would assert that everything beneath it was there and fine. That is the
              one claim this run is least entitled to make, and making it laundered the deletion.

              "Deleting an interior record is loud" is only true because something looks. This is
              that thing -- and it looks for a gap in the counter and a link that fails to meet,
              neither of which a SUFFIX removal produces.

              Refusing is a denial the operator can be locked out by, and that is the accepted trade
              -- the same one the money path already makes when its audit row cannot be written.
            */
            var chainState = await anchors.VerifyChainAsync(context, cancellationToken);
            if (!chainState.IsIntact)
            {
                return (NotRecorded, DescribeRefusal(chainState));
            }

            var tail = await anchors.ReadTailAsync(context, cancellationToken);

            var verification = await chain.VerifyAsync(context, cancellationToken);
            var record = anchors.Build(verification, tail, DateTime.UtcNow);

            context.Set<Shared.Entities.AuditAnchor>().Add(record);
            await context.SaveChangesAsync(cancellationToken);

            return (VerdictExitCode(verification), Describe(record, verification));
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            /*
              INTERRUPTION IS DECIDED BY THE TOKEN, NOT BY THE EXCEPTION TYPE — and this command
              shipped with that exactly backwards, in a file whose sibling had already written the
              correction down at length. VerifyCommand's equivalent guard says it plainly: catching
              OperationCanceledException covers only what a pre-cancelled token produces, which is
              what a unit test manufactures. Cancel a walk that is genuinely in flight and
              Microsoft.Data.SqlClient sends an attention, the server aborts the batch, and the task
              completes FAULTED with a SqlException.

              So this guard has to come FIRST, above every other catch, or the shape an operator
              actually produces falls through to one of them. It did: the store-failure handler added
              below caught it and reported Ctrl+C as "the audit store could not be read" — a database
              outage, over a command the operator stopped on purpose. That is worse than the escaping
              exception it replaced, because an escape is loud and a wrong sentence is not.

              It accepts the same trade knowingly: a genuine outage that coincides with a signalled
              token is reported as an interruption. That is the better error to make, because the
              operator who pressed Ctrl+C knows they did.
            */
            return (VerifyCommand.Interrupted, new[]
            {
                "INTERRUPTED: the walk was stopped, so nothing was recorded.",
                "  Part of the chain was read and the rest was not, which is not a verdict.",
                "  Nothing was written and nothing was changed. Re-run it; the walk is read-only",
                "  and costs only the time it takes.",
            });
        }
        catch (DbUpdateException)
        {
            /*
              THE CLUSTERED KEY TURNING A DOUBLE LAUNCH INTO A LOUD FAILURE, which is what it is for.
              Two runs racing produce the same next sequence and one of them loses here. It is not a
              verdict about the chain and must not be reported as one.
            */
            return (NotRecorded, new[]
            {
                "NOT RECORDED: another run wrote the next anchor first.",
                "  Nothing is wrong with the chain and nothing was changed by this run. Re-run it;",
                "  the walk is read-only and costs only the time it takes.",
            });
        }
        catch (Exception failure)
        {
            /*
              A STORE THAT CANNOT BE READ IS NOT A USAGE ERROR, and without this catch it was
              reported as one. MEASURED against an unreachable instance: `verify` exits 3 and prints
              "CANNOT VERIFY: the audit store could not be read"; `anchor` exited **4** -- this
              tool's word for "the command line was wrong" -- and dumped a raw .NET stack trace at
              the operator. The two commands answered the same outage with different numbers, and
              the wrong one was the number automation reads.

              The mechanism: only DbUpdateException and OperationCanceledException were caught, so a
              SqlException escaped RunAsync, System.CommandLine's exception handler turned it into 1,
              and CombineExitCodes maps any non-zero parser result to UsageError. Nothing was wrong
              with the command line.

              Misconfigured, matching verify, because the honest statement is the same one: there is
              no verdict, and it is not a statement about the chain.
            */
            return (VerifyCommand.Misconfigured, new[]
            {
                "NO VERDICT: the audit store could not be read, so nothing was recorded.",
                $"  {failure.GetType().Name}: {failure.Message}",
                "  This is NOT a statement about the chain, and nothing was written. Check the",
                "  connection string and the keys first. If they are right, preserve the database",
                "  and escalate -- a table that has vanished from a database where it belongs is",
                "  the most complete tamper anyone holding write access can manage.",
            });
        }
    }

    private static int VerdictExitCode(AuditChainVerification verification)
        => verification.IsIntact
            ? verification.Verified > 0 ? VerifyCommand.Intact : VerifyCommand.NothingToVerify
            : VerifyCommand.Broken;

    private static string[] DescribeRefusal(AuditAnchorChainVerification state)
    {
        var lines = new List<string>
        {
            "NOT RECORDED: the existing anchor chain did not verify, so nothing was appended.",
            $"  Records verified before the break: {state.Verified:N0}",
            $"  Broke at anchor: {state.FirstBrokenSequence:N0}",
            string.Empty,
            "  " + state.Reason,
            string.Empty,
        };

        lines.AddRange(state.Kind switch
        {
            AuditAnchorChainBreakKind.MissingRecord => new[]
            {
                "  A REMOVED RECORD IS THE ONE MOVE THIS CHAIN EXISTS TO MAKE LOUD, and this is it",
                "  being loud. Somebody with database access can delete records; they cannot write",
                "  replacements without Audit:AnchorKey, which is why the gap is still here.",
                "  Preserve the table and escalate. Compare what survives against the numbers you",
                "  wrote down off this machine -- those are the evidence, not the table.",
            },
            AuditAnchorChainBreakKind.UnknownScheme => new[]
            {
                "  The record was NOT checked, which is never the same as checked and good. If you",
                "  are holding the wrong key, that is a configuration problem and re-running with",
                "  the right one settles it. If you are not, it is a write.",
            },
            _ => new[]
            {
                "  Preserve the table and escalate. Nothing was written, because extending a chain",
                "  this run cannot vouch for would assert that everything beneath it was fine.",
            },
        });

        return [.. lines];
    }

    private static string[] Describe(Shared.Entities.AuditAnchor record, AuditChainVerification verification)
    {
        var lines = new List<string>();

        if (record.Kind == AuditAnchorKind.GapMarker)
        {
            lines.Add($"GAP MARKER {record.AnchorSequence:N0} recorded: this run found nothing to anchor.");
            lines.Add(verification.IsIntact
                ? "  The audit table has no rows. That is not the same as an intact chain, and"
                  + " recording"
                : "  The chain did not verify, so there is no state worth anchoring. Recording");
            lines.Add("  that this run happened is itself evidence -- a run that left no trace would");
            lines.Add("  be indistinguishable from a run nobody made.");
            lines.Add("  It covers NOTHING: every coverage field is empty, by construction.");
            return [.. lines];
        }

        lines.Add($"ANCHOR {record.AnchorSequence:N0} recorded.");
        lines.Add($"  Covers sequences {record.LowestCoveredSequence:N0} to "
            + $"{record.CoveredThroughSequence:N0}, {record.CoveredRowCount:N0} rows.");
        lines.Add(string.Empty);

        /*
          THE STRONGEST TRUE SENTENCE AVAILABLE HERE IS NOT AN AFFIRMATIVE ONE, and printing a green
          line would be the whole defect. No third party has attested anything: nothing is timestamped
          and nothing is published, so this record is only as good as the operator's own memory of it.

          THE PAIR, NOT THE COUNTER ALONE. This deployment is single-principal and holds the anchor
          key, so after truncating, somebody can simply re-run this mode until a bare counter regrows
          to the number on the paper -- with genuine authentication codes and a valid link every step
          of the way. CoveredThroughSequence is the one quantity that cannot be regrown downward.
        */
        lines.Add("  WRITE THESE TWO NUMBERS DOWN SOMEWHERE THIS MACHINE CANNOT REACH:");
        lines.Add($"      anchor {record.AnchorSequence:N0}  through sequence {record.CoveredThroughSequence:N0}");
        lines.Add("  The pair, not the counter alone -- a counter can be regrown by re-running this");
        lines.Add("  command, and the sequence it covers cannot be regrown downward.");
        lines.Add("  Or run `export <path>`, which writes every record to a file so the numbers");
        lines.Add("  do not depend on somebody copying them correctly. The file still has to");
        lines.Add("  LEAVE: written here, it is deleted in the same breath as the rows it counts.");
        lines.Add(string.Empty);
        lines.Add("  This record is consistent with an intact chain covering through that sequence.");
        lines.Add("  It is EVIDENCE only if those are the numbers you already had: nothing here is");
        lines.Add("  timestamped by anybody else, and whoever can truncate the table can also write");
        lines.Add("  records over the result.");

        return [.. lines];
    }
}
