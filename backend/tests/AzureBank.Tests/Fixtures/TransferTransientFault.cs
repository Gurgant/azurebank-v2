using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Where a one-shot transient fault is injected into a transfer, to exercise
/// the two failure sequences of the EF transient-retry bug (ADR-0009),
/// or into a deposit's audited save at its commit: as the commit starts (Case C),
/// or after it has landed (Case B).
/// </summary>
public enum TransferFaultMode
{
    /// <summary>
    /// Case A: throw at the transfer's FIRST business SaveChanges (the
    /// INSERT INTO [Transactions] batch), before anything commits. The
    /// execution strategy must retry the whole delegate cleanly — exactly one
    /// transaction pair, one debit/credit.
    /// </summary>
    AtFirstSaveChanges,

    /// <summary>
    /// Case B: let the transfer commit, then throw right AFTER the commit
    /// (commit ack lost). The database already holds the transfer + the
    /// Executed idempotency flip, so the retried delegate must NOT re-execute;
    /// it must signal 409 IDEMPOTENCY_RESULT_UNKNOWN. That holds for a transfer.
    /// A deposit's audited save asks instead whether its audit row landed, and
    /// the answer is 201.
    /// </summary>
    AfterCommit,

    /// <summary>
    /// Case C: let the business save run, then throw as its commit STARTS, before anything is sent
    /// to the server. The database commits nothing, and the execution strategy re-runs the
    /// delegate. Written for the audited save (a deposit), whose re-run found every change already
    /// accepted by the failed attempt and committed an empty transaction: a 201 with no money moved.
    /// </summary>
    BeforeCommit
}

/// <summary>
/// One-shot transient-fault coordinator shared between the command and
/// transaction interceptors below. Thread-safe: EF may drive commands and
/// commits from parallel requests, and the fault must fire at most once.
///
/// Disarmed until <see cref="Arm"/> is called (after the funding deposit, so
/// the funding INSERT INTO [Transactions] is not mistaken for the transfer's).
/// </summary>
public sealed class TransferTransientFault(TransferFaultMode mode)
{
    private int _armed;              // 0 = disarmed, 1 = armed
    private int _commandFired;       // one-shot latch (Case A)
    private int _commitFired;        // one-shot latch (Cases B and C)
    private volatile bool _transferInsertSeen;

    public TransferFaultMode Mode => mode;

    /// <summary>Arm the fault. Call AFTER funding, right before the transfer.</summary>
    public void Arm() => Interlocked.Exchange(ref _armed, 1);

    public bool Fired =>
        Volatile.Read(ref _commandFired) == 1 || Volatile.Read(ref _commitFired) == 1;

    private bool IsArmed => Volatile.Read(ref _armed) == 1;

    /// <summary>
    /// True at most once: the first transfer INSERT after arming, in
    /// <see cref="TransferFaultMode.AtFirstSaveChanges"/> mode.
    /// </summary>
    public bool ShouldFailCommandOnce(string commandText) =>
        mode == TransferFaultMode.AtFirstSaveChanges
        && IsArmed
        && IsTransferInsert(commandText)
        && Interlocked.CompareExchange(ref _commandFired, 1, 0) == 0;

    /// <summary>Records that the transfer's INSERT has executed (arms the commit fault).</summary>
    public void NoteCommandExecuted(string commandText)
    {
        if (mode is TransferFaultMode.AfterCommit or TransferFaultMode.BeforeCommit
            && IsArmed && IsTransferInsert(commandText))
        {
            _transferInsertSeen = true;
        }
    }

    /// <summary>
    /// True at most once: the first commit to start after the ledger INSERT, in
    /// <see cref="TransferFaultMode.BeforeCommit"/> mode.
    /// </summary>
    public bool ShouldFailCommittingOnce() =>
        mode == TransferFaultMode.BeforeCommit
        && IsArmed
        && _transferInsertSeen
        && Interlocked.CompareExchange(ref _commitFired, 1, 0) == 0;

    /// <summary>
    /// True at most once: the first commit after the transfer's INSERT, in
    /// <see cref="TransferFaultMode.AfterCommit"/> mode.
    /// </summary>
    public bool ShouldFailCommitOnce() =>
        mode == TransferFaultMode.AfterCommit
        && IsArmed
        && _transferInsertSeen
        && Interlocked.CompareExchange(ref _commitFired, 1, 0) == 0;

    private static bool IsTransferInsert(string commandText) =>
        commandText.Contains("[Transactions]", StringComparison.OrdinalIgnoreCase)
        && commandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Injects the transient fault at the transfer's first business SaveChanges
/// (Case A), or observes that INSERT so the transaction interceptor can fault
/// the commit (Cases B and C). A <see cref="TimeoutException"/> is transient to the
/// SqlServerRetryingExecutionStrategy, so no fragile SqlException reflection is
/// needed to trigger a real EF retry.
/// </summary>
public sealed class TransferCommandFaultInterceptor(TransferTransientFault fault) : DbCommandInterceptor
{
    private void FailIfArmed(DbCommand command)
    {
        if (fault.ShouldFailCommandOnce(command.CommandText))
        {
            throw new TimeoutException(
                "Injected one-shot transient fault at the transfer's first SaveChanges (test).");
        }
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        FailIfArmed(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        FailIfArmed(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        FailIfArmed(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        FailIfArmed(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    // Post-callbacks: note the ledger INSERT succeeded (Case B and C commit faults).
    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        fault.NoteCommandExecuted(command.CommandText);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        fault.NoteCommandExecuted(command.CommandText);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData,
        int result, CancellationToken cancellationToken = default)
    {
        fault.NoteCommandExecuted(command.CommandText);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(
        DbCommand command, CommandExecutedEventData eventData, int result)
    {
        fault.NoteCommandExecuted(command.CommandText);
        return base.NonQueryExecuted(command, eventData, result);
    }
}

/// <summary>
/// Faults a transaction commit: as it starts (Case C: nothing lands), or right
/// AFTER it has durably committed (Case B: commit ack lost). Throwing a
/// transient from the post-commit notification propagates out of CommitAsync;
/// the transfer's hardened catch preserves it (its Rollback is best-effort), so
/// the execution strategy sees the transient and retries the delegate against
/// an already-committed database.
/// </summary>
public sealed class TransferCommitFaultInterceptor(TransferTransientFault fault) : DbTransactionInterceptor
{
    // Case C: thrown as the commit starts, so the server commits nothing and the transaction is
    // rolled back when the strategy's delegate unwinds.
    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (fault.ShouldFailCommittingOnce())
        {
            throw new TimeoutException(
                "Injected one-shot transient fault as the commit started, before it was sent (test).");
        }

        return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
    }

    public override InterceptionResult TransactionCommitting(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        if (fault.ShouldFailCommittingOnce())
        {
            throw new TimeoutException(
                "Injected one-shot transient fault as the commit started, before it was sent (test).");
        }

        return base.TransactionCommitting(transaction, eventData, result);
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (fault.ShouldFailCommitOnce())
        {
            throw new TimeoutException(
                "Injected one-shot transient fault right after the transfer commit (test).");
        }

        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionCommitted(
        DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (fault.ShouldFailCommitOnce())
        {
            throw new TimeoutException(
                "Injected one-shot transient fault right after the transfer commit (test).");
        }

        base.TransactionCommitted(transaction, eventData);
    }
}
