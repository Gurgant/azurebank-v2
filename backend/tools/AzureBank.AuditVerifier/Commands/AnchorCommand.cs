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
/// buys is that DELETING anchors is loud, while MINTING one needs <c>Audit:AnchorKey</c>. The
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
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AuditOptions>>();

        /*
          VALIDATED AT THE POINT OF USE, exactly where verify validates its own key and for the same
          measured reason: doing it during host construction meant an unconfigured machine could not
          even print --help.
        */
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
            var tail = await anchors.ReadTailAsync(context, cancellationToken);

            /*
              AUTHENTICATE WHAT YOU ARE ABOUT TO BUILD ON, BEFORE BUILDING ON IT. Extending a record
              this run cannot vouch for would launder it: the new record's link would assert that the
              previous one was there and fine, which is exactly the claim this run is not in a
              position to make.

              Refusing is a denial the operator can be locked out by, and that is the accepted trade
              -- the same one the money path already makes when its audit row cannot be written.
            */
            if (tail is not null)
            {
                var check = anchors.Check(tail);
                if (check is not AuditAnchorCheck.Authentic)
                {
                    return (NotRecorded, DescribeRefusal(tail.AnchorSequence, check));
                }
            }

            var verification = await chain.VerifyAsync(context, cancellationToken);
            var record = anchors.Build(verification, tail, DateTime.UtcNow);

            context.Set<Shared.Entities.AuditAnchor>().Add(record);
            await context.SaveChangesAsync(cancellationToken);

            return (VerdictExitCode(verification), Describe(record, verification));
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
        catch (OperationCanceledException)
        {
            return (VerifyCommand.Interrupted, new[]
            {
                "INTERRUPTED: the walk was stopped, so nothing was recorded.",
                "  Part of the chain was read and the rest was not, which is not a verdict.",
            });
        }
    }

    private static int VerdictExitCode(AuditChainVerification verification)
        => verification.IsIntact
            ? verification.Verified > 0 ? VerifyCommand.Intact : VerifyCommand.NothingToVerify
            : VerifyCommand.Broken;

    private static string[] DescribeRefusal(long sequence, AuditAnchorCheck check) => check switch
    {
        AuditAnchorCheck.UnknownScheme => new[]
        {
            $"NOT RECORDED: anchor {sequence} names a scheme or a key this run cannot apply,",
            "  so its authenticity was NOT checked -- which is never the same as checked and good.",
            "  Either you hold a different Audit:AnchorKey than the run that wrote it, or this build",
            "  cannot render the version it declares, or the record was overwritten. Nothing was",
            "  written, because extending a record this run cannot vouch for would launder it.",
        },
        _ => new[]
        {
            $"NOT RECORDED: anchor {sequence} does not match its own authentication code,",
            "  and it names the key this run holds -- so the key is not in question. This is a WRITE.",
            "  Preserve the table and escalate. Nothing was written.",
        },
    };

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
        lines.Add(string.Empty);
        lines.Add("  This record is consistent with an intact chain covering through that sequence.");
        lines.Add("  It is EVIDENCE only if those are the numbers you already had: nothing here is");
        lines.Add("  timestamped by anybody else, and whoever can truncate the table can also write");
        lines.Add("  records over the result.");

        return [.. lines];
    }
}
