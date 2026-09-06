using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Funds an account from a SECOND connection at the one moment that matters for the closure's
/// guards-again call: after the first attempt has passed both guards on a zero balance and just as
/// its own soft-delete UPDATE goes to the server.
/// </summary>
/// <remarks>
/// <para>
/// The property under test (ADR-0049 D8) is that a deposit which lands between the guard and the
/// save does not get a funded account closed on the strength of a balance read before it. That
/// needs the race to be lost AFTER the guards ran and BEFORE the UPDATE committed, which is where
/// this fires: on the first command that updates <c>[Accounts]</c> after arming — the soft delete's
/// own UPDATE, issued from inside the closure's transaction — it first runs a raw UPDATE of the
/// account's <c>Balance</c> on a plain <see cref="SqlConnection"/> and only then lets the
/// intercepted command proceed. <c>RowVersion</c> is a SQL Server <c>rowversion</c> column, so the
/// raw UPDATE moves it; the intercepted UPDATE carries <c>WHERE RowVersion = </c> the value the
/// attempt read, matches zero rows, and EF raises <c>DbUpdateConcurrencyException</c> — the outer
/// loop in <c>AccountService.DeleteAccountAsync</c> then reloads a funded, open account and
/// re-enters the delegate, where the guards must answer 422.
/// </para>
/// <para>
/// Same shape as <see cref="OutOfBandStepUpConsumeInterceptor"/>, and a raw connection for the same
/// reason: every context the factory builds carries this very interceptor. The WHERE mirrors the
/// closure's own reading — an open account only — so the deposit is honest about funding something
/// that could still be closed.
/// </para>
/// <para>
/// No deadlock is possible: the closure's transaction has taken no lock on the account row when
/// this runs (its reload was a plain SELECT under READ COMMITTED, before the transaction opened;
/// the audit tail's UPDLOCK/HOLDLOCK is on <c>[AuditEvents]</c>). One-shot, so the retry the race
/// provokes is not funded a second time.
/// </para>
/// </remarks>
public sealed class OutOfBandDepositInterceptor(
    string connectionString, Guid accountId, decimal amount)
    : DbCommandInterceptor
{
    private int _armed;
    private int _fired;

    /// <summary>
    /// Arm after the mint, so the mint's own writes are not mistaken for the closure's.
    /// </summary>
    public void Arm() => Interlocked.Exchange(ref _armed, 1);

    /// <summary>
    /// True once the out-of-band deposit actually ran — else the test proves nothing.
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
        await DepositIfThisIsTheSoftDeleteAsync(command, cancellationToken);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await DepositIfThisIsTheSoftDeleteAsync(command, cancellationToken);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private async Task DepositIfThisIsTheSoftDeleteAsync(
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
        await using var deposit = connection.CreateCommand();
        deposit.CommandText =
            "UPDATE [Accounts] SET [Balance] = [Balance] + @amount "
            + "WHERE [Id] = @id AND [IsDeleted] = 0";
        deposit.Parameters.Add(new SqlParameter("@id", accountId));
        deposit.Parameters.Add(new SqlParameter("@amount", amount));
        OutOfBandRowsAffected = await deposit.ExecuteNonQueryAsync(cancellationToken);
    }
}
