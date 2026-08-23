using System.CommandLine;
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

            // The RANGE is reported alongside the count so the operator can compare it with what
            // they expected. A chain that verifies 40 rows when yesterday it had 40,000 is intact
            // and catastrophic, and only the numbers say so.
            var rows = context.Set<Shared.Entities.AuditEvent>().AsNoTracking();
            var lowest = await rows.MinAsync(e => (long?)e.Sequence, cancellationToken);
            var highest = await rows.MaxAsync(e => (long?)e.Sequence, cancellationToken);

            return Report(await chain.VerifyAsync(context, cancellationToken), lowest, highest);
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

        command.SetHandler(async () =>
        {
            var (exitCode, lines) = await RunAsync(services);

            foreach (var line in lines)
            {
                Console.WriteLine(line);
            }

            Environment.ExitCode = exitCode;
        });

        return command;
    }
}
