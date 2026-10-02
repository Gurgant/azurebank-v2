using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Fails one commit for real: the transaction that sent a chosen statement is rolled back, the
/// test then does what it wants done before the work is run again, and the caller is told with a
/// fault EF retries.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="LostCommitAnswerInterceptor"/>, for the other way a commit can go
/// wrong. There the work landed and only the answer was lost, so the retrying execution strategy
/// asks <c>verifySucceeded</c> and stops. Here nothing landed: the strategy asks, is told no, and
/// runs the work a second time, against whatever the database holds by then.
/// </para>
/// <para>
/// That second run is what this fixture is for. The first one got as far as its last line, so
/// anything it decided is still in the caller's hands when the second begins; a caller that
/// reports what the first run decided, after a second run that decided otherwise, is wrong.
/// <paramref name="beforeTheRetry"/> is where a test changes the database so that the two runs
/// differ. It runs after the rollback: the first run's locks are gone, so it never waits on them.
/// </para>
/// <para>
/// Armed when a command whose text contains every marker has run; fires once, on the next commit.
/// A bare <see cref="TimeoutException"/> is the fault, for the reason
/// <see cref="TransientFailureInterceptor"/> gives.
/// </para>
/// </remarks>
public sealed class FailedCommitInterceptor(Func<Task> beforeTheRetry, params string[] markers)
    : DbCommandInterceptor, IDbTransactionInterceptor
{
    private int _statementRan;
    private int _fired;

    /// <summary>True once a commit has actually been failed. False means the test proved nothing.</summary>
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

    public async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _statementRan) == 1 && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await beforeTheRetry();
            throw new TimeoutException("Injected: the commit failed and nothing of it landed (test).");
        }

        return result;
    }

    private void Note(DbCommand command)
    {
        if (markers.All(marker => command.CommandText.Contains(marker, StringComparison.Ordinal)))
        {
            Volatile.Write(ref _statementRan, 1);
        }
    }
}
