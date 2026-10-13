using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Makes every save of one account by ONE request lose the race for the account's
/// <c>RowVersion</c> while armed, and writes nothing: in each command that updates that account's
/// row and carries that request's idempotency key, it replaces the <c>RowVersion</c> the attempt
/// read with one no row holds.
/// </summary>
/// <remarks>
/// <para>
/// The UPDATE carries <c>WHERE [Id] = </c> the account <c> AND [RowVersion] = </c> the value its
/// attempt read. With that value replaced it matches zero rows, as it does when another write
/// took the row first, and EF raises <c>DbUpdateConcurrencyException</c>.
/// </para>
/// <para>
/// NOTHING IS WRITTEN, SO THE OTHER REQUESTS ARE LEFT AS THEY ARE. The row keeps its
/// <c>RowVersion</c>: no other request that writes the account loses an attempt to this fixture,
/// which <see cref="LostRowVersionRaceInterceptor"/>, by writing the row, would make them do. A
/// proof that sends many writes at once uses this one for the write it needs to lose.
/// </para>
/// <para>
/// ONE REQUEST. A money write's save carries its idempotency key, in the update of its record,
/// in the command that updates the account. A command that does not carry the armed key among
/// its parameters is left alone.
/// </para>
/// </remarks>
public sealed partial class StaleRowVersionInterceptor : DbCommandInterceptor
{
    /// <summary>A <c>rowversion</c> no row holds: a database counts them from above zero.</summary>
    private static readonly byte[] NoRowHoldsIt = new byte[8];

    private Guid? _accountId;
    private Guid _key;
    private int _fired;

    /// <summary>Starts making the saves of <paramref name="accountId"/> that carry <paramref name="key"/> lose.</summary>
    public void Arm(Guid accountId, Guid key)
    {
        _key = key;
        _accountId = accountId;
    }

    /// <summary>Stops: the next save meets the RowVersion it read.</summary>
    public void Disarm() => _accountId = null;

    /// <summary>How many saves were made to lose the race.</summary>
    public int Fired => Volatile.Read(ref _fired);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        ReplaceTheRowVersion(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ReplaceTheRowVersion(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    [GeneratedRegex(
        @"UPDATE \[Accounts\] SET [^;]*?WHERE \[Id\] = (?<id>@\w+) AND \[RowVersion\] = (?<version>@\w+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex AccountUpdate();

    private void ReplaceTheRowVersion(DbCommand command)
    {
        if (_accountId is not { } accountId
            || !command.Parameters.Cast<DbParameter>().Any(p => p.Value is Guid value && value == _key))
        {
            return;
        }

        foreach (Match update in AccountUpdate().Matches(command.CommandText))
        {
            if (command.Parameters[update.Groups["id"].Value].Value is Guid id && id == accountId)
            {
                command.Parameters[update.Groups["version"].Value].Value = NoRowHoldsIt;
                Interlocked.Increment(ref _fired);
            }
        }
    }
}
