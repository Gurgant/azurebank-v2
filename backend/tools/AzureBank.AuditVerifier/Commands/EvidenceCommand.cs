using System.CommandLine;
using System.CommandLine.Invocation;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
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
/// A successful mint writes no audit row — measured: <c>StepUpAuthorizationService</c> calls
/// <c>IAuditService</c> only to record a wrong or locked PIN (since 2026-09-14; this said "never
/// calls" until then), and the mint endpoint not at all — so the PIN proof lives only in a table
/// anybody holding the database can rewrite. What the chain vouches for is the
/// <c>MoneyTransferred</c> (or <c>MoneyTransferredInternally</c>) row naming the transaction and,
/// since 2026-09-14, the authorisation it consumed (<c>AuditDetails</c>, in the hashed
/// <c>Detail</c>), against which the pack checks the unchained pointer. The authorisation row
/// itself, with its instants, is still in an unchained table, and for a pre-binding row written
/// before that date the pointer is still the only tie. (This remark said the pointer was the only
/// tie, full stop, until that date.) Reporting the join as if the chain covered both halves would
/// be the green-and-false this repository treats as the worst state, so the pack reports each half
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

            /*
              THE BINDING THE CHAIN VOUCHES FOR (AuditDetails, since 2026-09-14). The success row
              names the authorisation it consumed, inside the hash; the pointer in the unchained
              authorisation table is checked against that name rather than trusted on its own. A
              row written before that date names none, and is reported as a pre-binding row,
              never as a finding.
            */
            // MoneyWithdrawn joins the two transfer events (ADR-0056): a withdrawal's success row
            // now carries ConsumedAuthorisation in its hashed Detail, so it is the row that names
            // the authorisation for a withdrawal exactly as MoneyTransferred does for a transfer.
            // Left out, the lookup below would find no success row for an honest withdrawal and
            // report a pre-binding row -- a verdict that reads as "written before we recorded
            // this" when the truth is "we forgot to look".
            var successRow = auditRows.FirstOrDefault(e =>
                e.Outcome == AuditOutcome.Succeeded
                && (e.Event == SecurityEvents.MoneyTransferred
                    || e.Event == SecurityEvents.MoneyTransferredInternally
                    || e.Event == SecurityEvents.MoneyWithdrawn));
            var boundId = AuditDetails.ConsumedAuthorisationOf(successRow?.Detail);
            var bound = boundId is { } named
                ? await context.StepUpAuthorizations
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == named, cancellationToken)
                : null;

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
            lines.AddRange(StrongAuthentication(
                movement,
                authorisation,
                hasSuccessRow: successRow is not null,
                detailUnreadable: successRow?.Detail is not null && boundId is null,
                boundId,
                bound));
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
        // With its unit, through the one formatter the server has for a figure a person reads:
        // invariant digits, then the ISO code. The pack is read outside the system, where "40.00"
        // alone names no currency; it printed exactly that until 2026-09-11.
        var amount = ValidationRules.DescribeAmount(movement.Amount);
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

    /// <summary>
    /// The second-factor verdict. <paramref name="authorisation"/> is what the unchained table's
    /// pointer says paid for the movement; <paramref name="boundId"/> is what the CHAINED audit
    /// row names, and <paramref name="bound"/> that row if it still exists. The chained name wins
    /// every disagreement, because it is the half a database writer cannot rewrite unnoticed.
    /// <paramref name="detailUnreadable"/> is the row that HAS a Detail the reader could not use:
    /// reported as such, never as a pre-binding row, since Detail is under the row's hash and the
    /// chain verdict below is what says whether to believe it.
    /// </summary>
    internal static IEnumerable<string> StrongAuthentication(
        Transaction movement,
        StepUpAuthorization? authorisation,
        bool hasSuccessRow,
        bool detailUnreadable,
        Guid? boundId,
        StepUpAuthorization? bound)
    {
        // WIDENED FOR THE WITHDRAWAL (ADR-0056). This read `is TransactionType.TransferOut`, so
        // a withdrawal -- which now mints and spends an authorisation exactly as a transfer does --
        // would have been answered "NO AUTHORISATION APPLIES" and its PIN proof declared absent by
        // the very verb an operator runs to find it.
        var appliesToType = movement.Type is TransactionType.TransferOut or TransactionType.Withdrawal;

        if (!appliesToType)
        {
            yield return $"NO AUTHORISATION APPLIES: a {movement.Type} carries no step-up"
                + " authorisation.";
            yield return "  ADR-0042 binds an authorisation to the two TRANSFER endpoints and";
            yield return "  ADR-0056 to the withdrawal; a deposit and the incoming leg of a transfer";
            yield return "  are not minted against. Ask for the OUTGOING leg's number to see the";
            yield return "  authorisation that paid for a transfer.";
            yield break;
        }

        if (boundId is null)
        {
            // A pre-binding row (or no success row at all): the pointer is all there is.
            if (authorisation is null)
            {
                yield return "NOT STRONGLY AUTHENTICATED: no consumed authorisation names this transaction.";
                yield return "  A transfer or a withdrawal cannot be accepted without one (ADR-0042 and";
                yield return "  ADR-0056 refuse it 401), so";
                yield return "  either this movement predates that rule, or the row that paid for it is";
                yield return "  gone -- and the table it lived in is NOT chained, so its absence leaves no";
                yield return "  break to find.";
            }
            else
            {
                /*
                  THE READER'S WORD, NOT THE ENUM'S. A withdrawal reaches this branch since
                  ADR-0056, and an evidence report that calls it a transfer is wrong in the one
                  place an operator is reading to find out what happened.

                  `movement.Type.ToString()` was the first attempt and it was worse than the bug:
                  `TransferOut` renders "transferout", so every transfer's report changed too.
                  EvidenceVerdictTests caught that, which is what it is for.
                */
                var noun = movement.Type == TransactionType.Withdrawal ? "withdrawal" : "transfer";
                yield return $"STRONGLY AUTHENTICATED: authorisation {authorisation.Id:D} paid for this"
                    + $" {noun}.";
                foreach (var line in Instants(authorisation))
                {
                    yield return line;
                }

                yield return "  ⚠️ This row is evidence the application wrote, and it is NOT inside the";
                yield return "  chain:";
                yield return "  minting writes no audit row, so the second factor is vouched for by a mutable";
                yield return "  table, not by a hash. The chain below covers the audit row that names the";
                yield return "  movement; it does not cover this one.";
            }

            if (detailUnreadable)
            {
                yield return "  Bound authorisation: UNREADABLE. The audit row for this movement carries a";
                yield return "  Detail that is not the shape the application writes, so the name it should";
                yield return "  carry cannot be read. Detail is under that row's hash: read the chain";
                yield return "  verdict below, and treat this as a finding whatever it says.";
            }
            else if (hasSuccessRow)
            {
                yield return "  Bound authorisation: none. The audit row for this movement is a pre-binding row,";
                yield return "  written before success rows began naming the authorisation they consumed";
                yield return "  (2026-09-14); the pointer above is all the binding this movement has.";
            }
            else
            {
                yield return "  Bound authorisation: none, because no Succeeded movement row names this";
                yield return "  movement (the rows below say what does).";
            }

            yield break;
        }

        if (bound is null)
        {
            yield return $"BOUND AUTHORISATION MISSING: the chained audit row names authorisation"
                + $" {boundId:D}, and no such row exists.";
            yield return "  The chain vouches for the NAME: this movement was paid for by that";
            yield return "  authorisation. The row that would show when the PIN was proved and spent";
            yield return "  is gone -- a write around the application, or a purge -- and its absence";
            yield return "  is the finding. The chain below stays intact, because that table was";
            yield return "  never inside it.";
            if (authorisation is not null)
            {
                yield return $"  ⚠️ A different authorisation, {authorisation.Id:D}, claims to have paid";
                yield return "  for this movement. The chained name is the one to believe.";
            }

            yield break;
        }

        /*
          THE TABLE IS NOT IN THE CHAIN, so every field this verdict leans on is checked, not only
          the pointer and the status. A row rewritten around the application can keep both of those
          and still lie about who minted it, what for, or whether it was ever spent -- and the first
          version of this check would have called such a row STRONGLY AUTHENTICATED (found in review,
          2026-09-15). The owner is the outgoing account's; the operation follows the movement's
          shape, a recipient handle marking the external rail; a consumed row always records its
          instant.
        */
        /*
          THE MOVEMENT'S TYPE IS ASKED FIRST, and that ordering is the whole correction (ADR-0056).
          This was a two-armed ternary over the recipient handle alone, and a withdrawal carries no
          handle -- so it would have fallen into the InternalTransfer arm and every honest
          withdrawal would have been reported as bound to the wrong operation. The handle still
          decides between the two transfer rails, where it is the thing that tells them apart.
        */
        var expectedOperation = movement.Type switch
        {
            TransactionType.Withdrawal => StepUpOperation.Withdrawal,
            _ => movement.RecipientAzureTag is null
                ? StepUpOperation.InternalTransfer
                : StepUpOperation.Transfer,
        };
        var reasons = new List<string>();
        if (bound.ConsumedByTransactionId != movement.Id)
        {
            reasons.Add(bound.ConsumedByTransactionId switch
            {
                { } other => $"  and that row says it paid for {other:D} instead.",
                null => $"  and that row records no movement against it (status {bound.Status}).",
            });
        }
        else if (bound.Status != StepUpAuthorizationStatus.Consumed)
        {
            reasons.Add($"  and that row points at this movement, but its status is {bound.Status}, not Consumed.");
        }

        if (bound.UserId != movement.Account.UserId)
        {
            reasons.Add($"  and that row was minted by {bound.UserId:D}, not by this account's owner"
                + $" {movement.Account.UserId:D}.");
        }

        if (bound.Operation != expectedOperation)
        {
            reasons.Add($"  and that row was minted for {bound.Operation}, not for the {expectedOperation}"
                + " this movement is.");
        }

        if (bound.ConsumedAt is null && bound.Status == StepUpAuthorizationStatus.Consumed)
        {
            reasons.Add("  and that row is marked Consumed but records no instant of spending.");
        }

        if (reasons.Count > 0)
        {
            yield return $"BOUND AUTHORISATION DOES NOT MATCH: the chained audit row names authorisation"
                + $" {boundId:D},";
            foreach (var reason in reasons)
            {
                yield return reason;
            }

            yield return "  The chained name is the evidence; the authorisation table was written";
            yield return "  around the application. Treat it as a finding.";
            if (authorisation is not null && authorisation.Id != bound.Id)
            {
                yield return $"  ⚠️ A different authorisation, {authorisation.Id:D}, claims this movement";
                yield return "  through the unchained pointer.";
            }

            yield break;
        }

        yield return "STRONGLY AUTHENTICATED, BOUND IN THE CHAIN: the audit row for this movement names"
            + $" authorisation {bound.Id:D}, and that row paid for it.";
        foreach (var line in Instants(bound))
        {
            yield return line;
        }

        yield return "  The NAME is inside the chain; the instants above are read from the";
        yield return "  authorisation row, which is not. Deleting or re-pointing that row is reported";
        yield return "  as a finding above rather than silently downgrading this verdict.";
        if (authorisation is not null && authorisation.Id != bound.Id)
        {
            yield return $"  ⚠️ A second authorisation, {authorisation.Id:D}, also claims this movement";
            yield return "  through the unchained pointer.";
        }
    }

    private static IEnumerable<string> Instants(StepUpAuthorization authorisation)
    {
        yield return $"  Operation {authorisation.Operation}, status {authorisation.Status}";
        yield return $"  PIN proved (minted) {authorisation.CreatedAt:O}";
        yield return $"  Spent (consumed)   {authorisation.ConsumedAt?.ToString("O") ?? "(never)"}";
        yield return $"  Window closed      {authorisation.ExpiresAt:O}";
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
