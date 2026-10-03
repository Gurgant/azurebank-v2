using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Forces an account-number collision in a statement that inserts several accounts at once, by
/// rewriting the first account number it carries to one that already exists.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DuplicateAccountNumberInterceptor"/> does the same for registration, which inserts
/// one account with <c>INSERT INTO [Accounts]</c>. A demo copy saves four accounts in one batch,
/// and EF may write those as one <c>MERGE [Accounts] … WHEN NOT MATCHED THEN INSERT</c> (it needs
/// each row's <c>RowVersion</c> back, in order), so that text is not guaranteed. This one matches
/// any command that names <c>[Accounts]</c> and inserts, whichever shape EF chose.
/// </para>
/// <para>
/// SQL Server raises the real 2601 against the real <c>IX_Accounts_AccountNumber</c>. By default it
/// fires ONCE and disarms, so the copy's retry goes through; <c>everyTime</c> keeps it armed, to
/// show where the retries stop.
/// </para>
/// </remarks>
public sealed class CollidingAccountNumberInterceptor : DbCommandInterceptor
{
    private readonly string _duplicateOf;
    private readonly bool _everyTime;
    private int _collisions;

    public CollidingAccountNumberInterceptor(string duplicateOf, bool everyTime = false)
    {
        _duplicateOf = duplicateOf;
        _everyTime = everyTime;
    }

    /// <summary>True once a collision has actually been injected.</summary>
    public bool Fired => Collisions > 0;

    /// <summary>How many statements were made to collide.</summary>
    public int Collisions => Volatile.Read(ref _collisions);

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
        if ((!_everyTime && Volatile.Read(ref _collisions) > 0)
            || !command.CommandText.Contains("[Accounts]", StringComparison.Ordinal)
            || !command.CommandText.Contains("INSERT", StringComparison.Ordinal))
        {
            return;
        }

        foreach (DbParameter parameter in command.Parameters)
        {
            // By value, not by parameter name: EF names insert parameters by position.
            if (parameter.Value is string value
                && value.StartsWith("AB-", StringComparison.Ordinal)
                && value != _duplicateOf)
            {
                // One winner when two commands race for the single shot.
                if (!_everyTime && Interlocked.CompareExchange(ref _collisions, 1, 0) != 0)
                {
                    return;
                }

                if (_everyTime)
                {
                    Interlocked.Increment(ref _collisions);
                }

                parameter.Value = _duplicateOf;
                return;
            }
        }
    }
}
