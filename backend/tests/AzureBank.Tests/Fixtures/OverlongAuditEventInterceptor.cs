using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Makes the first <c>INSERT INTO [AuditEvents]</c> carrying a given event name fail with a genuine
/// SQL Server error, by overflowing the <c>Event</c> column (capped at 40 in
/// <c>AuditEventConfiguration</c>).
/// </summary>
/// <remarks>
/// <para>
/// The twin of <see cref="OverlongAccountNameInterceptor"/>, and for the same reason: a REAL server
/// error — <c>String or binary data would be truncated</c> — rather than an exception thrown from
/// .NET before the command runs. An exception raised in the interceptor would never reach the
/// transaction, and would therefore prove nothing about rollback, which is the entire question
/// ADR-0044 D1 raises.
/// </para>
/// <para>
/// The event name is matched by VALUE rather than by parameter name: EF names insert parameters
/// positionally (<c>@p0</c>, <c>@p1</c>, …), and that ordering is an implementation detail which
/// would silently start rewriting a different column if it ever changed.
/// </para>
/// <para>
/// One-shot, so a retry — the API runs under <c>EnableRetryOnFailure</c> — proceeds normally on the
/// next attempt instead of being starved. A truncation error is non-transient, so nothing retries
/// it in practice; the guard is there so the test does not depend on that staying true.
/// </para>
/// </remarks>
public sealed class OverlongAuditEventInterceptor : DbCommandInterceptor
{
    /// <summary>Comfortably past the 40-character cap on <c>AuditEvents.Event</c>.</summary>
    private static readonly string TooLong = new('X', 400);

    private readonly string _eventName;
    private int _fired;

    public OverlongAuditEventInterceptor(string eventName) => _eventName = eventName;

    /// <summary>True once the failure has actually been injected.</summary>
    public bool Fired => Volatile.Read(ref _fired) == 1;

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
        if (Volatile.Read(ref _fired) == 1
            || !command.CommandText.Contains("INSERT INTO [AuditEvents]", StringComparison.Ordinal))
        {
            return;
        }

        foreach (DbParameter parameter in command.Parameters)
        {
            if (parameter.Value is not string value
                || !value.Equals(_eventName, StringComparison.Ordinal))
            {
                continue;
            }

            if (Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
            {
                return;
            }

            parameter.Value = TooLong;
            parameter.Size = TooLong.Length;
            return;
        }
    }
}
