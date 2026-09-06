using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Spends a step-up authorisation from a SECOND connection at the one moment that matters: after
/// the closure has validated it and just before the closure's own writes go to the server.
/// </summary>
/// <remarks>
/// <para>
/// Consuming the authorisation BEFORE the DELETE would prove nothing about the transaction:
/// <c>ValidateAsync</c> would see it Consumed and refuse before a single row was touched. The
/// property under test (ADR-0049) is that when <c>ConsumeAsync</c>'s conditional UPDATE matches
/// zero rows INSIDE the closure's transaction, the soft delete and the <c>AccountDeleted</c> row
/// that were already sent roll back with it. That needs the race to be lost between the
/// validation and the spend, which is where this fires: on the first command that updates
/// <c>[Accounts]</c> after arming — the soft delete's own UPDATE, issued from inside the
/// closure's transaction — it first runs the out-of-band consume on a plain
/// <see cref="SqlConnection"/> and only then lets the intercepted command proceed.
/// </para>
/// <para>
/// A raw connection rather than a second <c>DbContext</c> from the factory, because every context
/// the factory builds carries this very interceptor — the out-of-band UPDATE would re-enter it.
/// The status literal is the string EF stores (<c>HasConversion&lt;string&gt;</c> on
/// <c>StepUpAuthorization.Status</c>), and the WHERE mirrors <c>ConsumeAsync</c>'s: Pending only,
/// so the consume is honest about spending something that was spendable.
/// </para>
/// <para>
/// No deadlock is possible: <c>ValidateAsync</c> read the authorisation row without a transaction
/// (READ COMMITTED, no lock held), and the closure's transaction has not touched
/// <c>[StepUpAuthorizations]</c> yet when this runs. One-shot, so the retried delegate under
/// <c>EnableRetryOnFailure</c> (not used by the test that owns this, but harmless) is not starved.
/// </para>
/// </remarks>
public sealed class OutOfBandStepUpConsumeInterceptor(string connectionString, Guid authorizationId)
    : DbCommandInterceptor
{
    private int _armed;
    private int _fired;

    /// <summary>
    /// Arm after the mint, so the mint's own writes are not mistaken for the closure's.
    /// </summary>
    public void Arm() => Interlocked.Exchange(ref _armed, 1);

    /// <summary>
    /// True once the out-of-band consume actually ran — else the test proves nothing.
    /// </summary>
    public bool Fired => Volatile.Read(ref _fired) == 1;

    /// <summary>
    /// Rows the out-of-band UPDATE matched; must be 1 for the race to have been lost.
    /// </summary>
    public int OutOfBandRowsAffected { get; private set; }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await ConsumeIfThisIsTheSoftDeleteAsync(command, cancellationToken);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await ConsumeIfThisIsTheSoftDeleteAsync(command, cancellationToken);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private async Task ConsumeIfThisIsTheSoftDeleteAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _armed) != 1
            || !command.CommandText.Contains("UPDATE [Accounts]", StringComparison.Ordinal)
            || Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
        {
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var consume = connection.CreateCommand();
        consume.CommandText =
            "UPDATE [StepUpAuthorizations] SET [Status] = 'Consumed', [ConsumedAt] = SYSUTCDATETIME() "
            + "WHERE [Id] = @id AND [Status] = 'Pending'";
        consume.Parameters.Add(new SqlParameter("@id", authorizationId));
        OutOfBandRowsAffected = await consume.ExecuteNonQueryAsync(cancellationToken);
    }
}
