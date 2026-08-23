using System.CommandLine;
using System.CommandLine.Invocation;
using AzureBank.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
            };

            /*
              SUSPECT THE KEY BEFORE SUSPECTING AN ATTACKER, when the break is at the very first row.

              The row hash is an HMAC over Audit:ChainKey. A WRONG key is not distinguishable from
              tampering by any check this tool can make -- and unlike a missing or short key, it
              passes the options validation, because it is a perfectly well-formed secret. What it
              is not is the one this chain was written with.

              The tell is the position: a real tamper breaks WHERE it happened, somewhere in the
              table. A wrong key breaks at the first row every time, because the first hash it
              recomputes is already different. Saying so here costs nothing and saves an operator
              from opening an incident about an attacker who does not exist.
            */
            if (result.FirstBrokenSequence <= 1)
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
        try
        {
            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

            /*
              THE RANGE COMES FROM THE WALK, not from two extra queries. It is reported beside the
              count so the operator can compare them -- a chain that verifies 40 rows where yesterday
              it had 40,000 is intact and catastrophic, and only the numbers say so. That comparison
              is worthless if the two facts come from different instants: this asked the database for
              MIN and MAX before walking, so a row committed in between was counted but fell outside
              the range, and the tool could report 101 rows verified over a range ending at 100.
            */
            var verification = await chain.VerifyAsync(context, cancellationToken);

            return Report(verification, verification.LowestSequence, verification.HighestSequence);
        }
        catch (Exception failure)
        {
            /*
              THE OUTER EXCEPTION, NOT GetBaseException(). Measured against an unreachable server:
              the outer SqlException says "A network-related or instance-specific error occurred
              while establishing a connection to SQL Server", and the innermost is a Win32Exception
              saying only "The wait operation timed out". Drilling to the base gave the operator the
              less useful of the two.
            */
            return (Misconfigured, new[]
            {
                "CANNOT VERIFY: the audit store could not be read, so there is no verdict.",
                $"  {failure.GetType().Name}: {failure.Message}",
                "  This is NOT a statement about the chain. Check the connection string and the",
                "  key before reading anything into it.",
            });
        }
    }

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
