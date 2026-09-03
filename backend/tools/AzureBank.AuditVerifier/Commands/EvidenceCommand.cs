using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureBank.AuditVerifier.Commands;

/// <summary>
/// Assembles, for ONE transaction, the evidence that it was strongly authenticated and that the
/// record of it is intact: the ledger row, the consumed step-up authorisation that paid for it, the
/// audit rows that name it, and the chain verdict those rows sit inside.
/// </summary>
/// <remarks>
/// <para>
/// THE PSD2 ART. 72 READ, BY SUBJECT AND BY TIME. ADR-0042 records that a consumed
/// <c>StepUpAuthorization</c> row is "the evidence B3 assembles", and the retention plan says B3 joins
/// that row to the audit rows. This is that join, with the one thing a join alone cannot say added
/// underneath it: whether the audit rows it found are inside a chain that still verifies.
/// </para>
/// <para>
/// ⚠️ <b>THE AUTHORISATION ROW IS NOT INSIDE THE CHAIN, AND THE OUTPUT SAYS SO ON EVERY RUN.</b>
/// Minting an authorisation writes no audit row — measured: <c>StepUpAuthorizationService</c> never
/// calls <c>IAuditService</c>, and neither does the mint endpoint — so the PIN proof lives only in a
/// table anybody holding the database can rewrite. What the chain vouches for is the
/// <c>MoneyTransferred</c> row naming the transaction; what ties that row to a second factor is a
/// pointer in an unchained table. Reporting the join as if the chain covered both halves would be
/// the green-and-false this repository treats as the worst state, so the pack reports each half
/// with the guarantee it actually has.
/// </para>
/// <para>
/// ⚠️ <b>AN INTACT VERDICT IS NOT AN INCLUSION PROOF.</b> The anchor is a tail hash over the whole
/// prefix, not a Merkle tree, so this can say "these rows are in a chain that verified at this
/// instant" and cannot hand a third party a proof that one row is in the set without handing over
/// the range (<c>docs/audit-trail-against-real-practice.md</c> names the gap). The pack prints the
/// rows and the verdict; it does not claim the proof.
/// </para>
/// <para>
/// BY TRANSACTION NUMBER, NOT BY GUID, because the number is what leaves the system: the transfer
/// response carries <c>TransactionNumber</c> and no id, so it is what a customer, an operator or a
/// regulator actually holds. Resolved through the unique index on <c>TransactionNumber</c>, then
/// the owner through the account, then the authorisation through the index on the authorisation's
/// <c>UserId</c> filtered on <c>ConsumedByTransactionId</c>, then the audit rows through the index on
/// <c>SubjectId</c>. Every hop uses an index that already exists; none was added.
/// </para>
/// <para>
/// THE NUMBER IS NOT VALIDATED BY SHAPE. <c>IdGenerator.IsValidTransactionNumber</c> rejects the
/// 19- and 20-character forms rows carried before the check symbol widened them, and those rows are
/// exactly the ones an evidence request may name years later. Only a blank or NUL-bearing argument
/// is refused before the store is asked; whether a number exists is the store's answer.
/// </para>
/// <para>
/// NO EXIT CODE OF ITS OWN. The verdict about the CHAIN is still the verdict, which is the rule
/// <c>export</c> settled: a pack over a broken chain prints the pack and exits 1, because the break
/// is the incident and the pack is a reading of a table that has stopped verifying. A number the
/// store does not hold is a fact about the command line, so it is 4, like a path <c>export</c>
/// cannot use.
/// </para>
/// </remarks>
public static class EvidenceCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "evidence",
            "Assemble the evidence that one transaction was strongly authenticated and is intact.");

        var numberArgument = new Argument<string>(
            "transactionNumber",
            "The TXN-... number the transfer response returned.");
        command.AddArgument(numberArgument);

        command.SetHandler(async (InvocationContext invocation) =>
        {
            var number = invocation.ParseResult.GetValueForArgument(numberArgument);
            var (exitCode, lines) = await RunAsync(services, number, invocation.GetCancellationToken());

            // Recorded before printed, for the reason every sibling records: writing to a closed
            // stdout throws, an escaping exception becomes exit 1, and 1 means CHAIN BROKEN.
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
        string transactionNumber,
        CancellationToken cancellationToken)
    {
        // The argument is checked before the key, for the reason export records: it is the thing
        // the operator just typed and the cheapest mistake to name, and it needs no configuration.
        if (string.IsNullOrWhiteSpace(transactionNumber) || transactionNumber.Contains('\0'))
        {
            return (VerifyCommand.UsageError, new[]
            {
                "NOT ASSEMBLED: that is not a transaction number.",
                "  `evidence` needs the TXN-... number the transfer response returned. A blank",
                "  argument or one carrying a NUL character cannot name a row, so nothing was read.",
                "    evidence TXN-20260902-0000000101X",
            });
        }

        try
        {
            services.GetService<IStartupValidator>()?.Validate();
        }
        catch (OptionsValidationException invalid)
        {
            var reasons = new List<string>
            {
                "CANNOT ASSEMBLE: this tool is not configured to read the chain.",
            };
            reasons.AddRange(invalid.Failures.Select(failure => $"  {failure}"));
            return (VerifyCommand.Misconfigured, reasons.ToArray());
        }

        using var scope = services.CreateScope();

        /*
          RESOLVED INSIDE A TRY, for the transitive reason export records: building the context
          builds the ring, the ring can refuse, and an unguarded refusal here would exit 4 with a
          stack trace for a correct command line. All five verbs answer that refusal with 3.
        */
        IAuditChain chain;
        AzureBankDbContext context;
        try
        {
            chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();
            context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        }
        catch (AuditKeyRingException ring)
        {
            return VerifyCommand.RingNotConfigured(ring);
        }

        try
        {
            var trimmed = transactionNumber.Trim();

            var movement = await context.Transactions
                .AsNoTracking()
                .Include(t => t.Account)
                .FirstOrDefaultAsync(t => t.TransactionNumber == trimmed, cancellationToken);

            if (movement is null)
            {
                return (VerifyCommand.UsageError, new[]
                {
                    $"NOT ASSEMBLED: no transaction is numbered {trimmed}.",
                    "  Nothing in this store carries that number, so there is no movement to build",
                    "  evidence for. Check the number against the transfer response or the ledger;",
                    "  this says nothing about the chain, which was not walked.",
                });
            }

            var related = movement.RelatedTransactionId is { } relatedId
                ? await context.Transactions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == relatedId, cancellationToken)
                : null;

            /*
              THROUGH THE OWNER, NOT A SCAN. Every StepUpAuthorization lookup in the application is
              scoped by UserId, and that is the column with the index. The owner of the outgoing
              account is the actor who minted, so the consumed row -- if one exists -- is under that
              UserId with ConsumedByTransactionId equal to this movement.
            */
            var authorisation = await context.StepUpAuthorizations
                .AsNoTracking()
                .Where(a => a.UserId == movement.Account.UserId
                            && a.ConsumedByTransactionId == movement.Id)
                .OrderBy(a => a.ConsumedAt)
                .FirstOrDefaultAsync(cancellationToken);

            var auditRows = await context.AuditEvents
                .AsNoTracking()
                .Where(e => e.SubjectId == movement.Id)
                .OrderBy(e => e.Sequence)
                .ToListAsync(cancellationToken);

            var verification = await chain.VerifyAsync(context, cancellationToken);
            var (chainCode, chainLines) = VerifyCommand.Report(
                verification, verification.LowestSequence, verification.HighestSequence);

            var lines = new List<string>
            {
                $"EVIDENCE PACK for {movement.TransactionNumber}",
                string.Empty,
            };

            lines.AddRange(Movement(movement, related));
            lines.Add(string.Empty);
            lines.AddRange(StrongAuthentication(movement, authorisation));
            lines.Add(string.Empty);
            lines.AddRange(AuditRows(movement, auditRows));
            lines.Add(string.Empty);
            lines.Add("Chain, as walked by this run -- the rows above are evidence only inside a");
            lines.Add("chain that verifies, and this is that verdict, rendered by `verify`:");
            lines.AddRange(chainLines.Select(l => "  " + l));
            lines.Add(string.Empty);
            lines.Add("  An intact verdict says these rows are in a chain that verified at this");
            lines.Add("  instant. It is NOT an inclusion proof: the anchor is a tail hash, not a tree,");
            lines.Add("  so handing a third party proof that ONE row is in the set means handing over");
            lines.Add("  the range.");

            return (chainCode, lines.ToArray());
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The token, not the exception type, for the reason verify measured: an in-flight
            // cancellation on SQL Server arrives as a SqlException, not an OperationCanceledException.
            return (VerifyCommand.Interrupted, new[]
            {
                "INTERRUPTED: the evidence was not assembled.",
                "  This says nothing about the store, the key or the transaction. If you stopped it",
                "  because it seemed to hang, the hang is the thing to look at. Otherwise run it again.",
            });
        }
        catch (Exception failure)
        {
            var cause = failure;
            for (var current = failure; current is not null; current = current.InnerException)
            {
                if (current is System.Data.Common.DbException)
                {
                    cause = current;
                    break;
                }
            }

            return (VerifyCommand.Misconfigured, new[]
            {
                "CANNOT ASSEMBLE: the store could not be read, so there is no evidence and no",
                "  verdict.",
                $"  {cause.GetType().Name}: {cause.Message}",
                "  This is NOT a statement about the transaction or the chain. A wrong connection",
                "  string and a table that is no longer there exit the same way; check the",
                "  connection string and the keys first, and if they are right, preserve the",
                "  database and escalate rather than re-running migrations, which recreate tables",
                "  and erase evidence.",
            });
        }
    }

    private static IEnumerable<string> Movement(Transaction movement, Transaction? related)
    {
        yield return "Movement, from the ledger row (readable by anyone holding the database; not";
        yield return "hashed, and printed here because Art. 72 asks what moved, not only that";
        yield return "something did):";
        var amount = movement.Amount.ToString("F2", CultureInfo.InvariantCulture);
        yield return $"  {movement.Type} of {amount} on account {movement.AccountId:D},"
            + $" status {movement.Status}";
        yield return $"  Created {movement.CreatedAt:O}";
        if (movement.RecipientAzureTag is { } recipient)
        {
            yield return $"  Recipient handle: {recipient}";
        }

        if (related is not null)
        {
            yield return $"  Other leg: {related.TransactionNumber}"
                + $" ({related.Type} on account {related.AccountId:D})";
        }
    }

    private static IEnumerable<string> StrongAuthentication(
        Transaction movement, StepUpAuthorization? authorisation)
    {
        var appliesToType = movement.Type is TransactionType.TransferOut;

        if (!appliesToType)
        {
            yield return $"NO AUTHORISATION APPLIES: a {movement.Type} carries no step-up"
                + " authorisation.";
            yield return "  ADR-0042 binds an authorisation to the two TRANSFER endpoints only; a";
            yield return "  deposit, a withdrawal and the incoming leg of a transfer are not minted";
            yield return "  against. Ask for the OUTGOING leg's number to see the authorisation that";
            yield return "  paid for a transfer.";
            yield break;
        }

        if (authorisation is null)
        {
            yield return "NOT STRONGLY AUTHENTICATED: no consumed authorisation names this transaction.";
            yield return "  A transfer cannot be accepted without one (ADR-0042 refuses it 401), so";
            yield return "  either this movement predates that rule, or the row that paid for it is";
            yield return "  gone -- and the table it lived in is NOT chained, so its absence leaves no";
            yield return "  break to find.";
            yield break;
        }

        yield return $"STRONGLY AUTHENTICATED: authorisation {authorisation.Id:D} paid for this"
            + " transfer.";
        yield return $"  Operation {authorisation.Operation}, status {authorisation.Status}";
        yield return $"  PIN proved (minted) {authorisation.CreatedAt:O}";
        yield return $"  Spent (consumed)   {authorisation.ConsumedAt?.ToString("O") ?? "(never)"}";
        yield return $"  Window closed      {authorisation.ExpiresAt:O}";
        yield return "  ⚠️ This row is evidence the application wrote, and it is NOT inside the";
        yield return "  chain:";
        yield return "  minting writes no audit row, so the second factor is vouched for by a mutable";
        yield return "  table, not by a hash. The chain below covers the audit row that names the";
        yield return "  movement; it does not cover this one.";
    }

    private static IEnumerable<string> AuditRows(Transaction movement, IReadOnlyList<AuditEvent> rows)
    {
        if (rows.Count == 0)
        {
            yield return "NO AUDIT ROW names this transaction.";
            yield return "  Every money movement writes one in the SAME transaction as the ledger row";
            yield return "  (ADR-0044 D1), so a ledger row with no audit row is a movement recorded";
            yield return "  without its record -- a write around the application, or a purge. Treat it";
            yield return "  as a finding, whatever the chain verdict below says.";
            yield break;
        }

        yield return $"Audit rows naming this transaction: {rows.Count}";
        foreach (var row in rows)
        {
            yield return $"  #{row.Sequence:N0} {row.Event} -> {row.Outcome} at {row.OccurredAt:O}";
            var actor = row.ActorUserId?.ToString("D") ?? "(none)";
            yield return $"      actor {actor}, payload {row.PayloadVersion},"
                + $" key {row.KeyId ?? "(no identity recorded)"}";
        }
    }
}
