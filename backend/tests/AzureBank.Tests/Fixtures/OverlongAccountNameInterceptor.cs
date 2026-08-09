using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Makes the first <c>INSERT INTO [Accounts]</c> fail with a genuine, NON-TRANSIENT, NON-COLLISION
/// SQL Server error, by overflowing the account <c>Name</c> column.
///
/// <para>
/// The point is the KIND of failure. <see cref="DuplicateAccountNumberInterceptor"/> provokes 2601,
/// which ADR-0036 taught the registration path to retry; this one provokes a truncation error
/// (<c>String or binary data would be truncated</c>), which nothing retries and nothing recovers.
/// It therefore stands in for the whole class ADR-0036 left open — a command timeout, a dropped
/// connection, a constraint nobody anticipated — while being deterministic enough to assert on.
/// </para>
/// <para>
/// A REAL server error rather than an exception thrown from the interceptor, because the question
/// under test is what the database and EF do together: an exception raised in .NET before the
/// command runs would never reach the transaction at all, and would prove nothing about rollback.
/// </para>
/// <para>
/// One-shot, so a retry — if anything ever adds one — proceeds normally instead of being starved.
/// </para>
/// </summary>
public sealed class OverlongAccountNameInterceptor : DbCommandInterceptor
{
    /// <summary>Comfortably past <c>ValidationRules.AccountNameMaxLength</c> (100).</summary>
    private static readonly string TooLong = new('X', 400);

    private int _fired;

    /// <summary>True once the failure has actually been injected.</summary>
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
            || !command.CommandText.Contains("INSERT INTO [Accounts]", StringComparison.Ordinal))
        {
            return;
        }

        foreach (DbParameter parameter in command.Parameters)
        {
            // Identify the column by its VALUE, not its parameter name: EF names insert parameters
            // positionally (@p0, @p1, …) and that ordering is an implementation detail which would
            // silently start rewriting the wrong column if it changed. Registration's account is
            // always named "Primary Account".
            if (parameter.Value is not string { Length: > 0 } value
                || !value.Equals("Primary Account", StringComparison.Ordinal))
            {
                continue;
            }

            if (Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
            {
                return;
            }

            parameter.Value = TooLong;
            parameter.Size = TooLong.Length;
            return;
        }
    }
}
