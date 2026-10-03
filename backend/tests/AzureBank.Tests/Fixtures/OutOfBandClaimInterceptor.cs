using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Claims a free demo copy from a SECOND connection at the one moment that matters: after the
/// recycler has chosen it as stale, and just before the recycler's own guard reaches the server.
/// </summary>
/// <remarks>
/// <para>
/// The recycler takes a stale free copy with a conditional
/// <c>UPDATE … WHERE Id = @id AND ClaimedAt IS NULL</c>, the statement a visitor's claim uses, so
/// whichever commits first wins and the other affects no row. Claiming the copy before the run would
/// prove nothing: the recycler would never choose it. This fires on the first command that updates
/// <c>[DemoCopies]</c> under the condition <c>[ClaimedAt] IS NULL</c>: it commits a claim on a plain
/// <see cref="SqlConnection"/>, then lets the intercepted command go, which must now affect no row.
/// </para>
/// <para>
/// A raw connection, as <see cref="OutOfBandStepUpConsumeInterceptor"/> uses: a second context from
/// the same container would carry this interceptor. One-shot.
/// </para>
/// </remarks>
public sealed class OutOfBandClaimInterceptor(string connectionString, Guid copyId) : DbCommandInterceptor
{
    private int _fired;

    /// <summary>True once the out-of-band claim actually ran: else the test proves nothing.</summary>
    public bool Fired => Volatile.Read(ref _fired) == 1;

    /// <summary>Rows the out-of-band claim affected; 1 means the visitor won.</summary>
    public int OutOfBandRowsAffected { get; private set; }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await ClaimIfThisIsTheGuardAsync(command, cancellationToken);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await ClaimIfThisIsTheGuardAsync(command, cancellationToken);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private async Task ClaimIfThisIsTheGuardAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var text = command.CommandText;
        if (!text.Contains("UPDATE", StringComparison.Ordinal)
            || !text.Contains("[DemoCopies]", StringComparison.Ordinal)
            || !text.Contains("[ClaimedAt] IS NULL", StringComparison.Ordinal)
            || Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
        {
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var claim = connection.CreateCommand();
        claim.CommandText =
            "UPDATE [DemoCopies] SET [ClaimedAt] = SYSUTCDATETIME(), [ClaimId] = NEWID() "
            + "WHERE [Id] = @id AND [ClaimedAt] IS NULL";
        claim.Parameters.Add(new SqlParameter("@id", copyId));
        OutOfBandRowsAffected = await claim.ExecuteNonQueryAsync(cancellationToken);
    }
}
