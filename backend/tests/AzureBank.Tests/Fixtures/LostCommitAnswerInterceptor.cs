using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Loses the answer to one commit: the transaction that sent a chosen statement commits, and the
/// caller is told it failed, with a fault EF retries.
/// </summary>
/// <remarks>
/// <para>
/// What a dropped connection does at the worst moment. The database holds the work; the retrying
/// execution strategy sees a transient fault and, before it runs the work again, asks the caller's
/// <c>verifySucceeded</c> whether it already landed. This is the fixture that makes that question
/// get asked.
/// </para>
/// <para>
/// <see cref="TransferTransientFault"/> does the same for a transfer, keyed on the ledger's INSERT.
/// This one is keyed on any statement: it arms when a command whose text contains every marker has
/// run, and fires once, right after the next commit. A bare <see cref="TimeoutException"/> is the
/// fault, for the reason <see cref="TransientFailureInterceptor"/> gives.
/// </para>
/// </remarks>
public sealed class LostCommitAnswerInterceptor(params string[] markers) : DbCommandInterceptor, IDbTransactionInterceptor
{
    private int _statementRan;
    private int _fired;

    /// <summary>True once an answer has actually been lost. False means the test proved nothing.</summary>
    public bool Fired => Volatile.Read(ref _fired) == 1;

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Note(command);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Note(command);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _statementRan) == 1 && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
        {
            throw new TimeoutException("Injected: the commit landed and its answer was lost (test).");
        }

        return Task.CompletedTask;
    }

    private void Note(DbCommand command)
    {
        if (markers.All(marker => command.CommandText.Contains(marker, StringComparison.Ordinal)))
        {
            Volatile.Write(ref _statementRan, 1);
        }
    }
}
