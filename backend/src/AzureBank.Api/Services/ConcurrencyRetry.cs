using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
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
        IsUniqueViolationOn(ex, TransactionNumberIndex, attempt);

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
        IsUniqueViolationOn(ex, AccountNumberIndex, attempt);

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
        AzureBankDbContext context, Account account, ILogger logger, Guid userId)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await context.SaveChangesAsync();
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
                    "AccountNumberCollision", userId, attempt);

                account.AccountNumber = IdGenerator.GenerateAccountNumber();
            }
        }
    }

    /// <summary>
    /// Walks the exception chain for a SQL Server unique violation raised by ONE named index.
    ///
    /// <para>
    /// Shared by both predicates so the narrowing is written once. The index name is the whole
    /// point: 2601/2627 are raised by every unique index in the schema, including the idempotency
    /// claim (a distributed lock, where a retry double-executes) and the registration duplicates
    /// above. SQL Server puts the constraint name in the message, which the SQL-gated proofs assert,
    /// so a message-format change fails a test rather than silently widening this.
    /// </para>
    /// </summary>
    private static bool IsUniqueViolationOn(Exception ex, string indexName, int attempt)
    {
        if (attempt >= MaxAttempts)
        {
            return false;
        }

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
        AzureBankDbContext context, params Account[] accounts)
    {
        await ResetToStoreAsync(context, accounts);
        await Task.Delay(Random.Shared.Next(5, 30));
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
    /// Deliberately does NOT touch the tracked IdempotencyRecord: its pending
    /// Executed flip must survive to ride the next SaveChanges (a
    /// ChangeTracker.Clear would drop it). No transactions other than the
    /// pair(s) created inside the delegate are ever tracked here, so detaching
    /// all of them is safe.
    /// </summary>
    public static async Task ResetToStoreAsync(
        AzureBankDbContext context, params Account[] accounts)
    {
        foreach (var entry in context.ChangeTracker.Entries<Transaction>().ToList())
        {
            entry.State = EntityState.Detached;
        }

        foreach (var account in accounts)
        {
            await context.Entry(account).ReloadAsync();
        }
    }
}
