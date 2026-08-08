using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Forces exactly ONE transaction-number collision, by rewriting the first INSERT's
/// <c>TransactionNumber</c> parameter to a value that already exists.
///
/// <para>
/// A collision cannot be provoked any other way. The number comes from a static
/// <c>IdGenerator</c> with 32⁷ values per day, so waiting for a natural one is not a test strategy,
/// and the recovery path would otherwise be unreachable code that only a comment claims works.
/// </para>
/// <para>
/// Rewriting the parameter is faithful where it matters: SQL Server raises the real 2601 against
/// the real <c>IX_Transactions_TransactionNumber</c>, so the production predicate — which matches
/// on the error number AND the index name — is exercised exactly as it would be in the wild.
/// </para>
/// <para>
/// One-shot by design. It disarms after firing so the retry proceeds normally; a permanently-armed
/// interceptor would prove only that the loop gives up.
/// </para>
/// </summary>
public sealed class DuplicateTransactionNumberInterceptor : DbCommandInterceptor
{
    private readonly string _duplicateOf;
    private int _fired;

    public DuplicateTransactionNumberInterceptor(string duplicateOf) => _duplicateOf = duplicateOf;

    /// <summary>True once the collision has actually been injected.</summary>
    public bool Fired => Volatile.Read(ref _fired) == 1;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Rewrite(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Rewrite(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Rewrite(DbCommand command)
    {
        if (Volatile.Read(ref _fired) == 1
            || !command.CommandText.Contains("INSERT INTO [Transactions]", StringComparison.Ordinal))
        {
            return;
        }

        foreach (DbParameter parameter in command.Parameters)
        {
            // Identify the column by its VALUE rather than its parameter name: EF names insert
            // parameters positionally (@p0, @p1, …), and that ordering is an implementation detail
            // that would silently start rewriting the wrong column if it ever changed.
            if (parameter.Value is string value
                && value.StartsWith("TXN-", StringComparison.Ordinal)
                && value != _duplicateOf)
            {
                // CompareExchange, not read-then-write: the early return above and a separate write
                // are two operations, so two concurrent commands could both pass the check and both
                // inject. Today's tests are sequential, so it would not fire — but "exactly ONE" is
                // the property this fixture sells, and a second injection would make the retry
                // assertions quietly meaningless rather than fail.
                if (Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
                {
                    return;
                }

                parameter.Value = _duplicateOf;
                return;
            }
        }
    }
}
