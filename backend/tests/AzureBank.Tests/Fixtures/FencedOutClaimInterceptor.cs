using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Takes a request's idempotency claim away while armed: before each command that updates
/// <c>[IdempotencyRecords]</c>, it gives the record under one key a new <c>ClaimId</c> from a
/// second connection, as a takeover of a stale claim does.
/// </summary>
/// <remarks>
/// <para>
/// A money request carries its record's flip to <c>Executed</c> in the save that moves the money,
/// fenced by <c>WHERE ClaimId = </c> the value it claimed with. With that value gone the UPDATE
/// matches zero rows and EF raises <c>DbUpdateConcurrencyException</c> naming the record: the
/// conflict of a request whose claim is no longer its own, which is not a race for an account.
/// </para>
/// <para>
/// A raw connection, because every context the factory builds carries this interceptor. The
/// request has not touched the record's row inside its transaction when the save is sent, so the
/// statement commits by itself before the save runs.
/// </para>
/// </remarks>
public sealed class FencedOutClaimInterceptor(string connectionString) : DbCommandInterceptor
{
    private Guid? _key;
    private int _fired;

    /// <summary>Starts taking away the claim on <paramref name="key"/>.</summary>
    public void Arm(Guid key) => _key = key;

    /// <summary>Stops.</summary>
    public void Disarm() => _key = null;

    /// <summary>How many saves met a record whose claim had just been taken.</summary>
    public int Fired => Volatile.Read(ref _fired);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await TakeTheClaimAsync(command, cancellationToken);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await TakeTheClaimAsync(command, cancellationToken);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private async Task TakeTheClaimAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (_key is not { } key
            || !command.CommandText.Contains("UPDATE [IdempotencyRecords]", StringComparison.Ordinal))
        {
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var take = connection.CreateCommand();
        take.CommandText = "UPDATE [IdempotencyRecords] SET [ClaimId] = NEWID() WHERE [Key] = @key";
        take.Parameters.Add(new SqlParameter("@key", key));
        if (await take.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            Interlocked.Increment(ref _fired);
        }
    }
}
