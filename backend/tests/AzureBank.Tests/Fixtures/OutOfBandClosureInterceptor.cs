using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Closes an account from a second connection, once, on the first command after arming whose text
/// holds the trigger.
/// </summary>
/// <remarks>
/// <para>
/// The default trigger is the request's own <c>UPDATE [Accounts]</c>: the closure then lands after
/// the request read the account and before its save, the save loses the account's
/// <c>RowVersion</c>, and the request goes round its retry. Another trigger places the closure
/// earlier, for example on the read of the step-up authorisation, which comes after the request
/// looked at its accounts and before it reloads them.
/// </para>
/// <para>
/// A raw connection, because every context the factory builds carries this interceptor. The
/// closure is the statement a closure leaves behind and nothing else: no audit row and no spent
/// authorisation, since the proofs are about what the racing request does with the closed row.
/// With <c>emptyFirst</c> the same statement also sets the balance to zero, which is the row a
/// withdrawal of everything followed by a closure leaves: the only closed row the API can produce
/// for an account that held money.
/// </para>
/// </remarks>
public sealed class OutOfBandClosureInterceptor(
    string connectionString, Guid accountId, string trigger = "UPDATE [Accounts]", bool emptyFirst = false)
    : DbCommandInterceptor
{
    private int _armed;
    private int _fired;

    /// <summary>
    /// Arms the interceptor so earlier setup writes are ignored.
    /// </summary>
    public void Arm() => Interlocked.Exchange(ref _armed, 1);

    /// <summary>
    /// True once the out-of-band closure update executes.
    /// </summary>
    public bool Fired => Volatile.Read(ref _fired) == 1;

    /// <summary>
    /// Rows matched by the out-of-band closure UPDATE command.
    /// </summary>
    public int OutOfBandRowsAffected { get; private set; }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await CloseIfThisIsTheTriggerAsync(command, cancellationToken);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await CloseIfThisIsTheTriggerAsync(command, cancellationToken);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private async Task CloseIfThisIsTheTriggerAsync(
        DbCommand command, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _armed) != 1
            || !command.CommandText.Contains(trigger, StringComparison.Ordinal)
            || Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
        {
            return;
        }

        OutOfBandRowsAffected = await CloseAsync(connectionString, accountId, emptyFirst, cancellationToken);
    }

    /// <summary>
    /// The closure itself, on a connection of its own; returns the rows it matched. A control uses
    /// it to close, before the request, an account the API refuses to close: a primary one.
    /// </summary>
    public static async Task<int> CloseAsync(
        string connectionString, Guid accountId, bool emptyFirst = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var close = connection.CreateCommand();
        close.CommandText =
            "UPDATE [Accounts] SET "
            + (emptyFirst ? "[Balance] = 0, " : string.Empty)
            + "[IsDeleted] = 1, [DeletedAt] = @now, [UpdatedAt] = @now "
            + "WHERE [Id] = @id AND [IsDeleted] = 0";
        close.Parameters.Add(new SqlParameter("@id", accountId));
        close.Parameters.Add(new SqlParameter("@now", DateTime.UtcNow));
        return await close.ExecuteNonQueryAsync(cancellationToken);
    }
}
