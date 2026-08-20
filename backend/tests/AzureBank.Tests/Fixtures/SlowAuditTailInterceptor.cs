using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Delays the audit chain's TAIL READ — the one statement that holds
/// <c>UPDLOCK, HOLDLOCK</c> on <c>AuditEvents</c> for the rest of its transaction — so the cost of
/// that lock can be measured rather than argued about.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0044 D1 makes every money movement wait on this read. The question it raises, and which
/// nothing had measured, is not whether ONE slow audit write is slow — obviously it is — but whether
/// it blocks OTHER money movements that have nothing to do with it. The tail is a single row, so the
/// lock is global to the table: if it does block them, an audit store that merely gets slow takes the
/// whole bank down with it, and the fail-closed posture costs far more than it looks.
/// </para>
/// <para>
/// ONE-SHOT and signalled. Only the first tail read is delayed, and <see cref="Fired"/> lets a test
/// assert the delay actually happened rather than reporting on a run where the interceptor never
/// matched — the difference between a measurement and a green square.
/// </para>
/// <para>
/// Matched on the WITH (UPDLOCK hint rather than on the table name alone, because ordinary reads of
/// AuditEvents — the assertions in every test — must not be slowed. The hint is what makes this
/// statement the one that matters.
/// </para>
/// </remarks>
public sealed class SlowAuditTailInterceptor : DbCommandInterceptor
{
    private readonly TimeSpan _delay;
    private int _fired;

    public SlowAuditTailInterceptor(TimeSpan delay) => _delay = delay;

    /// <summary>True once the delay has actually been applied to a tail read.</summary>
    public bool Fired => Volatile.Read(ref _fired) == 1;

    /*
      AFTER the read, not before it, and the distinction is the entire measurement. Delaying
      ReaderExecuting would stall the statement before SQL Server ever takes UPDLOCK, HOLDLOCK — a
      slow request that holds nothing, which tells us about latency and nothing about contention.
      Delaying ReaderExecuted stalls with the lock ALREADY HELD and the transaction still open,
      which is the state a hung or slow audit store actually puts the money path into.
    */
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        await DelayIfTailReadAsync(command, cancellationToken);
        return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    private async Task DelayIfTailReadAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (!command.CommandText.Contains("[AuditEvents] WITH (UPDLOCK", StringComparison.Ordinal))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
        {
            return;
        }

        await Task.Delay(_delay, cancellationToken);
    }
}
