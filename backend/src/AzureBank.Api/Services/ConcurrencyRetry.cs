using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using Microsoft.EntityFrameworkCore;

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
