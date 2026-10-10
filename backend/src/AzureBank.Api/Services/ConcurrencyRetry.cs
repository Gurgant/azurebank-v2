using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Utilities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureBank.Api.Services;

/// <summary>
/// Retry policy for optimistic-concurrency conflicts on Account balances
/// (defense in depth, independent of idempotency): parallel operations on
/// the SAME account race on its RowVersion; the loser reloads and recomputes
/// instead of surfacing a 500.
/// </summary>
internal static class ConcurrencyRetry
{
    public const int MaxAttempts = 8;

    /// <summary>
    /// Retries only genuine Account RowVersion races. A conflict involving
    /// an IdempotencyRecord means our claim was fenced out (a stale-claim
    /// takeover happened): retrying would DOUBLE-EXECUTE — always rethrow.
    /// </summary>
    public static bool ShouldRetry(DbUpdateConcurrencyException ex, int attempt) =>
        attempt < MaxAttempts
        && ex.Entries.Count > 0
        && ex.Entries.All(e => e.Entity is Account);

    /// <summary>
    /// SQL Server unique-violation numbers: 2627 = PRIMARY KEY / UNIQUE
    /// constraint, 2601 = duplicate row in a unique index. Same pair
    /// <see cref="Implementations.IdempotencyService"/> and
    /// <see cref="Implementations.UserService"/> already key on.
    /// </summary>
    private const int SqlPrimaryKeyViolation = 2627;
    private const int SqlUniqueIndexViolation = 2601;

    /// <summary>The index the generated transaction number lands in.</summary>
    private const string TransactionNumberIndex = "IX_Transactions_TransactionNumber";

    /// <summary>
    /// True when a write failed because the generated <c>TransactionNumber</c>
    /// was already taken — the one unique violation on a money path that a
    /// retry can legitimately clear, because the next attempt mints a new
    /// number.
    ///
    /// <para>
    /// <b>Narrowed by INDEX NAME, not just by error number.</b> 2601/2627 also
    /// carry the idempotency claim race, which is a distributed lock: retrying
    /// that would double-execute, which is exactly what
    /// <see cref="ShouldRetry"/> refuses to do for the RowVersion case. Matching
    /// the number alone would quietly convert the safest guard in the system
    /// into a retry loop. SQL Server puts the constraint name in the message —
    /// asserted by <c>TransactionNumberUniquenessSqlServerTests</c>, so a
    /// message-format change fails a test rather than silently widening this.
    /// </para>
    /// <para>
    /// EF does NOT retry these itself: probing the shipped
    /// <c>SqlServerTransientExceptionDetector</c> (10.0.1) shows 2601 and 2627
    /// are both non-transient, so <c>EnableRetryOnFailure</c> will not re-run
    /// the operation and this catch is the only recovery.
    /// </para>
    /// </summary>
    public static bool IsTransactionNumberCollision(Exception ex, int attempt) =>
        attempt < MaxAttempts && IsUniqueViolationOn(ex, TransactionNumberIndex);

    /// <summary>The index the generated account number lands in.</summary>
    private const string AccountNumberIndex = "IX_Accounts_AccountNumber";

    /// <summary>
    /// True when a write failed because the generated <c>AccountNumber</c> was already taken — the
    /// account-side twin of <see cref="IsTransactionNumberCollision"/>, and regenerable for the
    /// same reason: the next attempt mints a new number.
    ///
    /// <para>
    /// <b>The narrowing matters more here than it did for transactions.</b> The registration path
    /// can violate the AzureTag and NormalizedEmail unique indexes too, and those are the
    /// enumeration-neutral 409 in <c>AuthService</c> (ADR-0013) — retrying one of those would spin
    /// on a genuine duplicate and, worse, turn a deliberate security response into a loop. Matching
    /// the error number alone would do exactly that, so this matches the INDEX NAME as well.
    /// </para>
    /// <para>
    /// <b>Why it is worth catching at all, measured rather than argued.</b> Before this, an injected
    /// collision on registration produced: <c>500</c> to the client, the ApplicationUser COMMITTED
    /// with its role assigned, ZERO accounts owned, and a <c>409</c> on every retry with the same
    /// details — because the pre-checks then find the user's own row. An unrecoverable, account-less
    /// user, which is strictly worse than the transaction case: that one left the caller free to try
    /// the deposit again. <c>AccountNumberCollisionSqlServerTests</c> pins both paths.
    /// </para>
    /// </summary>
    public static bool IsAccountNumberCollision(Exception ex, int attempt) =>
        attempt < MaxAttempts && IsUniqueViolationOn(ex, AccountNumberIndex);

    /// <summary>The unique indexes a registration can legitimately lose at write time.</summary>
    private const string AzureTagIndex = "IX_AspNetUsers_AzureTag";

    /// <summary>Identity's own index name; made unique and NULL-filtered by AddUniqueEmailIndex.</summary>
    private const string NormalizedEmailIndex = "EmailIndex";

    /// <summary>
    /// True when a registration INSERT lost the AzureTag or NormalizedEmail unique-index race — the
    /// ONLY write failures that may become the enumeration-neutral 409 of ADR-0013.
    ///
    /// <para>
    /// <b>Every other <c>DbUpdateException</c> must propagate</b>, and since ADR-0037 that stopped
    /// being merely a labelling question. The catch it guards now sits INSIDE the execution-strategy
    /// delegate, so the inner <c>SaveChanges</c> no longer runs its own retry loop and the outer
    /// delegate is the only retry point left. The strategy decides by walking the exception's
    /// <c>InnerException</c> chain, and <c>ConflictException</c> carries none — so an unnarrowed
    /// catch does not just mislabel a deadlock (1205, transient in the shipped detector) as a
    /// duplicate, it SUPPRESSES the retry the delegate was restructured to make safe.
    /// </para>
    /// <para>
    /// <c>UserNameIndex</c> and <c>PK_AspNetUsers</c> are deliberately absent: UserName mirrors a
    /// freshly minted UUIDv7, so a violation on either is a Guid collision — a defect that must
    /// surface as a 500, not be reported to the caller as "these details are taken".
    /// </para>
    /// <para>
    /// Both names are read from the migrations rather than guessed, and pinned by
    /// <c>RegistrationDuplicateSqlServerTests</c> against real violations: a typo here would turn a
    /// genuine race loser into a 500, and no other test would notice.
    /// </para>
    /// </summary>
    public static bool IsRegistrationDuplicate(Exception ex) =>
        IsUniqueViolationOn(ex, AzureTagIndex) || IsUniqueViolationOn(ex, NormalizedEmailIndex);

    /// <summary>
    /// True when a write lost the AzureTag unique-index race specifically — the rename path's
    /// narrowing, as opposed to <see cref="IsRegistrationDuplicate"/> which also accepts the email
    /// index because a registration can lose either.
    ///
    /// <para>
    /// <b>The index name stopped being cosmetic here when the audit trail landed.</b>
    /// <c>UserService</c> used to match 2601/2627 by NUMBER alone, which was defensible while the
    /// only unique index that save could violate was the handle's. Since ADR-0044 the audit row
    /// rides that same <c>SaveChanges</c>, and <c>AuditEvents</c> carries its own unique index
    /// (<c>IX_AuditEvents_Sequence</c>) that raises the very same numbers — so a hash-chain
    /// collision would have been reported to the caller as "that handle is already taken", a 409
    /// for a fault that has nothing to do with them.
    /// </para>
    /// </summary>
    public static bool IsAzureTagCollision(Exception ex) => IsUniqueViolationOn(ex, AzureTagIndex);

    /// <summary>
    /// Saves a newly-added <see cref="Account"/>, minting a fresh number and retrying if the
    /// generated one was already taken.
    ///
    /// <para>
    /// Shared by both creation paths — registration and <c>CreateAccountAsync</c> — rather than
    /// copied into each, because the two differ only in what they log. A failed
    /// <c>SaveChangesAsync</c> leaves the entity in the <c>Added</c> state, so assigning a new
    /// number and saving again re-runs the same INSERT; there is nothing to detach or reload, which
    /// is why this does not go through <see cref="PrepareNextAttemptAsync"/> (that exists to undo
    /// balance mutations, and there are none here).
    /// </para>
    /// <para>
    /// Gives up after <see cref="MaxAttempts"/> and lets the exception escape. At 7.29e9 values a
    /// second consecutive clash is already absurd; eight in a row means something is wrong that a
    /// ninth attempt will not fix, and a silent infinite loop on a registration would be worse than
    /// the 500.
    /// </para>
    /// </summary>
    public static async Task SaveNewAccountAsync(
        AzureBankDbContext context, Account account, ILogger logger, Guid userId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException ex) when (IsAccountNumberCollision(ex, attempt))
            {
                // Logged at Warning as a SecurityEvent for the same reason the transaction-number
                // collision is: it should be effectively unobservable, so if it ever appears in a
                // real log the entropy assumption behind the format needs re-examining, not the
                // retry. The account id is not logged — it is not assigned until the INSERT lands.
                logger.LogWarning(ex,
                    "SecurityEvent {SecurityEvent}: generated account number was already taken for "
                        + "user {UserId}, regenerating (attempt {Attempt})",
                    SecurityEvents.AccountNumberCollision, userId, attempt);

                account.AccountNumber = IdGenerator.GenerateAccountNumber();
            }
        }
    }

    /// <summary>
    /// Walks the exception chain for a SQL Server unique violation raised by ONE named index.
    ///
    /// <para>
    /// Shared by all three predicates so the narrowing is written once. The MaxAttempts cap lives in
    /// the two RETRY predicates rather than here: a classifier answering "is this a duplicate?" must
    /// not change its answer with an attempt counter. The index name is the whole
    /// point: 2601/2627 are raised by every unique index in the schema, including the idempotency
    /// claim (a distributed lock, where a retry double-executes) and the registration duplicates
    /// above. SQL Server puts the constraint name in the message, which the SQL-gated proofs assert,
    /// so a message-format change fails a test rather than silently widening this.
    /// </para>
    /// </summary>
    private static bool IsUniqueViolationOn(Exception ex, string indexName)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql
                && sql.Errors.Cast<SqlError>().Any(
                    e => (e.Number == SqlUniqueIndexViolation || e.Number == SqlPrimaryKeyViolation)
                        && e.Message.Contains(indexName, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Discards the failed (never-persisted) transaction rows, reloads the
    /// accounts from the database (fresh balance + RowVersion) and applies a
    /// short random jitter so parallel losers do not stampede into the same
    /// conflict again.
    /// </summary>
    public static async Task PrepareNextAttemptAsync(
        AzureBankDbContext context, Account[] accounts, CancellationToken cancellationToken)
    {
        await ResetToStoreAsync(context, accounts, cancellationToken);
        await Task.Delay(Random.Shared.Next(5, 30), cancellationToken);
    }

    /// <summary>
    /// Resets the shared DbContext to database truth before a fresh attempt:
    /// detaches every tracked <see cref="Transaction"/> left by a failed
    /// attempt (Added by this attempt, or Unchanged if a prior attempt's
    /// SaveChanges already accepted the rows before its transaction rolled
    /// back) and reloads the accounts (fresh balance + RowVersion).
    ///
    /// Used both by the optimistic-concurrency retry above and by the transfer
    /// delegate, which the EF execution strategy re-runs on a transient fault
    /// (EnableRetryOnFailure) against this SAME context — the leftover Added
    /// transactions + already-mutated balances would otherwise be re-applied,
    /// producing duplicate transactions and a double debit/credit.
    ///
    /// ALSO detaches every tracked <see cref="AuditEvent"/>, unconditionally (ADR-0044, B1). A row
    /// added inside an attempt describes THAT attempt, so it dies with the ledger rows it was
    /// written about; leaving it attached let a deposit that took three attempts commit three rows
    /// claiming three deposits. Safe for the same reason the transaction detach is: no AuditEvent
    /// other than the attempt's own is ever tracked on these paths, because the four other writers
    /// never share a request scope with a money movement.
    ///
    /// Deliberately does NOT touch the tracked IdempotencyRecord: its pending
    /// Executed flip must survive to ride the next SaveChanges (a
    /// ChangeTracker.Clear would drop it). No transactions other than the
    /// pair(s) created inside the delegate are ever tracked here, so detaching
    /// all of them is safe.
    ///
    /// The summary said only "Transaction" until 2026-08-20, because the edit that added the
    /// AuditEvent behaviour matched an anchor that did not exist and silently changed nothing —
    /// leaving the new rule in a body comment that IntelliSense does not show. That is the same
    /// shape of stale contract doc ADR-0044 blames for the overcount going unasked in the first
    /// place.
    /// </summary>
    public static async Task ResetToStoreAsync(
        AzureBankDbContext context, Account[] accounts, CancellationToken cancellationToken)
    {
        foreach (var entry in context.ChangeTracker.Entries<Transaction>().ToList())
        {
            entry.State = EntityState.Detached;
        }

        /*
          AND THE ATTEMPT'S AUDIT ROW WITH THEM (ADR-0044, B1). An AuditEvent added inside an attempt
          describes THAT attempt — a movement that did not happen — so it has to die with the ledger
          rows it was written about, for the same reason and by the same rule. Leaving it attached
          would let a deposit that took three attempts commit three rows claiming three deposits.

          This is the exact opposite treatment from the IdempotencyRecord noted in the summary
          ABOVE, and the distinction is worth holding: the idempotency flip is about the REQUEST, which survives
          every attempt, while an audit row is about the ATTEMPT, which does not.
        */
        foreach (var entry in context.ChangeTracker.Entries<AuditEvent>().ToList())
        {
            entry.State = EntityState.Detached;
        }

        foreach (var account in accounts)
        {
            await context.Entry(account).ReloadAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Refuses a money write when one of the caller's own accounts came back closed from a reload,
    /// or did not come back at all, with the 404 the ownership check gives an account that was
    /// closed before the request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ResetToStoreAsync"/> reloads each account by its key, and a reload does not apply
    /// the filter that hides closed accounts from every query. An account closed after the request
    /// first read it therefore comes back as an ordinary tracked row with <c>IsDeleted</c> set, and
    /// a write that looks only at its balance moves money on a closed account.
    /// </para>
    /// <para>
    /// A ROW THAT IS GONE IS NOT IN THE FLAG. A reload that finds no row detaches the entry and
    /// leaves the entity as it was, <c>IsDeleted</c> false and the old balance in it. A write that
    /// adds a ledger row naming that entity then inserts the account again. So the entry's state is
    /// read too, which is why this takes the context: an account the request holds is tracked from
    /// the ownership check on, and only a reload that found nothing detaches it.
    /// </para>
    /// <para>
    /// The money write calls this itself, after the reload, and the reload does not: an idempotent
    /// attempt reads its claim after reloading, and a write that committed and lost its
    /// acknowledgement must answer from that claim, also when the account closed in between.
    /// </para>
    /// <para>
    /// For the caller's own accounts only. The message names the account, and a payer must not
    /// learn a payee's account id: <c>TransferService</c> handles a payee's account its own way,
    /// with <see cref="IsClosedOrGone"/>.
    /// </para>
    /// </remarks>
    public static void RefuseIfClosed(AzureBankDbContext context, params Account[] accounts)
    {
        foreach (var account in accounts)
        {
            if (IsClosedOrGone(context, account))
            {
                throw new NotFoundException("Account", account.Id);
            }
        }
    }

    /// <summary>
    /// True when the account, as the last reload left it, cannot take a money write: the row is
    /// closed, or the reload found no row and detached the entry.
    /// </summary>
    public static bool IsClosedOrGone(AzureBankDbContext context, Account account) =>
        account.IsDeleted || context.Entry(account).State == EntityState.Detached;

    /// <summary>
    /// Prepares one more attempt of an idempotent money operation: resets the accounts to the store
    /// and then decides, from the tracked <see cref="IdempotencyRecord"/>, whether re-executing is
    /// safe at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the CASE B half of the retry story. Case A is a stale <c>RowVersion</c>: nothing
    /// committed, reload and recompute. Case B is a transient fault AFTER the commit -- the
    /// database applied the work and the acknowledgement was lost -- and there the same delegate
    /// must NOT run again, because running it would move the money twice under one
    /// <c>Idempotency-Key</c>. The two are indistinguishable from the exception, so the claim row
    /// is what tells them apart.
    /// </para>
    /// <para>
    /// ONE COPY, deliberately. This logic lived privately in <c>TransferService</c> and the
    /// withdrawal needed it verbatim when it joined the step-up rail (ADR-0056). A second copy of a
    /// rule this sharp is the drift this class was created to prevent -- the same reason
    /// <see cref="IsTransactionNumberCollision"/> is here rather than inline at four call sites.
    /// </para>
    /// </remarks>
    /// <exception cref="IdempotencyException">
    /// Result unknown, 409 RESULT_UNKNOWN on the wire: this operation must not be executed again.
    /// A record reloaded as committed, under the request hash this request claimed with, carries
    /// <c>applied: true</c>; a claim row that vanished under us, or that was replaced by a record
    /// claimed with another body, carries no <c>applied</c>, because nothing is proven there.
    /// (Until 2026-10-01 this named the committed record and the vanished row as one answer:
    /// "a prior attempt committed, or the claim row vanished under us".) The detail tells a
    /// missing row from another request's record under the key.
    /// </exception>
    public static async Task PrepareIdempotentAttemptAsync(
        AzureBankDbContext context, Account[] accounts, CancellationToken cancellationToken)
    {
        await ResetToStoreAsync(context, accounts, cancellationToken);

        var entry = context.ChangeTracker.Entries<IdempotencyRecord>().FirstOrDefault();
        if (entry is null)
        {
            return;
        }

        // The body this request claimed the key with. Kept before the reload, which overwrites the
        // tracked value with whatever record holds the key now.
        var claimedHash = entry.Entity.RequestHash;

        // Fresh database truth for this claim. ReloadAsync also refreshes the tracked ORIGINAL
        // values, so the flip re-applied below emits a fenced UPDATE (WHERE ClaimId = <db value>)
        // that rides this attempt's commit.
        await entry.ReloadAsync(cancellationToken);

        // Every refusal below is a refusal to execute again. What differs is what can be SAID, and
        // the order of the tests is the point: after a reload that finds no row, entry.Entity
        // still holds this request's own pending flip to Executed, so a test of the status made
        // first would answer "applied" for a row that is gone.
        if (entry.State == EntityState.Detached)
        {
            // The row was deleted under us (stale takeover/cleanup): what entry.Entity says is
            // this request's own pending flip, not the database. We cannot prove that nothing
            // committed, and we cannot prove that something did.
            throw IdempotencyException.ResultUnknown();
        }

        if (entry.Entity.RequestHash != claimedHash)
        {
            // A record is there and it is not this request's. The reload is by key, and the key
            // was claimed again with another body: this request's claim was taken over as stale
            // and released by a request that was refused, and with no record left there was no
            // hash to refuse the other body on. What that record says is about those bytes.
            // Executed or Completed there is not this request's payment, and Processing there is
            // not this request's claim to re-arm. Nothing is proven about this request, but the
            // detail must say the key holds another request's record, not that no row is there.
            throw IdempotencyException.ResultUnknownReplaced();
        }

        if (entry.Entity.Status is IdempotencyStatus.Executed or IdempotencyStatus.Completed)
        {
            // Read from the database just now, under the hash this request claimed with: a commit
            // of these bytes under this key landed. The record under the key is not always this
            // request's claim. It is Executed after an earlier attempt of this request whose
            // acknowledgement was lost, and it is Executed or Completed after another request
            // with the same key and bytes took a stale claim over and committed (Completed once
            // that request stored its answer). "The same bytes" is the comparison above, not an
            // assumption.
            throw IdempotencyException.ResultUnknownApplied();
        }

        // Processing: nothing committed yet. Re-arm the pending Executed flip so it travels
        // atomically with this attempt's business commit.
        entry.Entity.Status = IdempotencyStatus.Executed;
        entry.Entity.ClaimId = Guid.NewGuid();
    }
}
