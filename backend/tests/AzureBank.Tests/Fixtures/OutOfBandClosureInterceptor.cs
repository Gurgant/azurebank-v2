using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Closes an account from a second connection when the first UPDATE on accounts executes after arming.
/// </summary>
public sealed class OutOfBandClosureInterceptor(
    string connectionString, Guid accountId)
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
        await CloseIfThisIsTheAccountUpdateAsync(command, cancellationToken);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await CloseIfThisIsTheAccountUpdateAsync(command, cancellationToken);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private async Task CloseIfThisIsTheAccountUpdateAsync(
        DbCommand command, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _armed) != 1
            || !command.CommandText.Contains("UPDATE [Accounts]", StringComparison.Ordinal)
            || Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
        {
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var close = connection.CreateCommand();
        close.CommandText =
            "UPDATE [Accounts] SET [IsDeleted] = 1, [DeletedAt] = @now, [UpdatedAt] = @now "
            + "WHERE [Id] = @id AND [IsDeleted] = 0";
        close.Parameters.Add(new SqlParameter("@id", accountId));
        close.Parameters.Add(new SqlParameter("@now", DateTime.UtcNow));
        OutOfBandRowsAffected = await close.ExecuteNonQueryAsync(cancellationToken);
    }
}
