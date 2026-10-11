using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Makes an audited save's question "did my commit land?" answer no, once, while armed: the read
/// that looks for the save's own audit row is not sent, and a result that says no row is there is
/// handed back in its place.
/// </summary>
/// <remarks>
/// <para>
/// After a commit that failed with a fault EF retries, the audited save asks whether its audit row
/// is there before the execution strategy runs it again (<c>AzureBankDbContext.SaveChangesAsync</c>).
/// That read can miss a commit that has landed (ADR-0058, residual risk 6), and the save then
/// runs a second time over work the database holds. With <see cref="LostCommitAnswerInterceptor"/>,
/// which lands a commit and loses its answer, this fixture produces that second run.
/// </para>
/// <para>
/// The read is recognised by its text: an <c>EXISTS</c> over <c>[AuditEvents]</c>, which no other
/// statement of a deposit sends.
/// </para>
/// </remarks>
public sealed class MissedCommitCheckInterceptor : DbCommandInterceptor
{
    private int _armed;
    private int _fired;

    /// <summary>Starts: the next such read answers that no row is there.</summary>
    public void Arm() => Volatile.Write(ref _armed, 1);

    /// <summary>True once a read was answered no. False means the test proved nothing.</summary>
    public bool Fired => Volatile.Read(ref _fired) == 1;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _armed) == 1
            && command.CommandText.Contains("EXISTS", StringComparison.Ordinal)
            && command.CommandText.Contains("[AuditEvents]", StringComparison.Ordinal)
            && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
        {
            var noRow = new DataTable();
            noRow.Columns.Add("Value", typeof(bool));
            noRow.Rows.Add(false);
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(noRow.CreateDataReader()));
        }

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
