using System.CommandLine;
using System.Data.Common;
using System.CommandLine.Invocation;
using AzureBank.Infrastructure.Data;
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
    /// Turns a verification result into what the operator sees and what a script reads.
    /// </summary>
    /// <remarks>
    /// Separated from the command so the three outcomes can be asserted without a console, a
    /// database or a process exit. The mapping IS the tool -- the rest is plumbing -- and an
    /// untested mapping is how "intact" ends up printed over an empty table.
    /// </remarks>
    public static (int ExitCode, IReadOnlyList<string> Lines) Report(
        AuditChainVerification result, long? lowest, long? highest)
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

              KEYED ON THE COUNT, NOT ON THE NUMBER 1, and the difference is a live table's whole
              life. This tested FirstBrokenSequence <= 1, which is the first row only while the
              first row has never been removed. Sequence is assigned as tail + 1 and never restarts
              (AuditChain.Link), so after any retention purge, archival or partial restore the chain
              begins at 5,001 or 900,204 -- and the hint silently stopped firing, on precisely the
              tables old enough to have been purged. Verified == 0 says "it broke before verifying
              anything" whatever the numbering, and it is printed on the line above.

              AND ONLY FOR A HASH MISMATCH, which is the only break a wrong key can cause. It cannot
              make a row unreadable, and it cannot change what a row records as its predecessor -- so
              on a deleted prefix or a poisoned column the hint was sending an operator to check a
              key that was never the problem. Measured on both.

              The row hash is an HMAC over Audit:ChainKey. A WRONG key is not distinguishable from
              tampering by any check this tool can make -- and unlike a missing or short key, it
              passes the options validation, because it is a perfectly well-formed secret. What it
              is not is the one this chain was written with.

              The tell is the position: a real tamper breaks WHERE it happened, somewhere in the
              table. A wrong key breaks at the first row every time, because the first hash it
              recomputes is already different. Saying so here costs nothing and saves an operator
              from opening an incident about an attacker who does not exist.
            */
            if (result.Verified == 0 && result.Kind == AuditChainBreakKind.HashMismatch)
            {
                lines.Add("  Breaking at the FIRST row usually means the wrong Audit:ChainKey, not");
                lines.Add("  tampering -- a wrong key is well-formed, so validation cannot catch it,");
                lines.Add("  and it mismatches from row one. Confirm the key before escalating.");
            }

            lines.Add("  Do NOT repair by deleting rows: see docs/runbooks/audit-chain-unavailable.md");
            return (Broken, lines);
        }

        if (result.Verified == 0)
        {
            return (NothingToVerify, new[]
            {
                "NOTHING TO VERIFY: the audit table has no rows.",
                "  This is not the same as an intact chain. An empty chain links perfectly,",
                "  so a table truncated to nothing reports exactly what a fresh one does.",
            });
        }

        return (Intact, new[]
        {
            $"CHAIN INTACT: {result.Verified:N0} rows verified.",
            $"  Sequence range: {lowest:N0} to {highest:N0}",
            "  This proves no row was altered and none was removed from the MIDDLE. It does",
            "  NOT prove none was removed from the END -- truncation needs no key and leaves",
            "  every surviving row linking correctly. Compare the count against your own.",
        });
    }

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
            var verification = await chain.VerifyAsync(context, cancellationToken);

            return Report(verification, verification.LowestSequence, verification.HighestSequence);
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
                "  Nothing is wrong with the store or the key -- part of the chain was checked and",
                "  the rest was not. Run it again to get an answer.",
            });
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
                "  This is NOT a statement about the chain. Check the connection string and the",
                "  key before reading anything into it.",
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
