using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Holds the first read whose command text contains a marker, before it is sent, until the test
/// releases it: a request parked between its arrival and its read of the database.
/// </summary>
/// <remarks>
/// <para>
/// Written for ADR-0057 §10 O2b, "a renewal in flight is not theft": the renewal is received,
/// stamped and then held BEFORE its read of the grant, a revoke of that grant commits in the gap,
/// and the renewal then reads a grant revoked after it arrived. That order cannot be produced by
/// timing two requests; it has to be made.
/// </para>
/// <para>
/// Held before the command is sent, so the read sees whatever committed while it waited — the
/// point of the gap. One-shot: <see cref="Held"/> completes for the first matching read only, and
/// every later read passes untouched. Bounded: a read the test forgets to release goes on after
/// <see cref="MaxHold"/>, so a broken test fails on its assertions instead of hanging the suite.
/// </para>
/// </remarks>
public sealed class HoldingReadInterceptor(string marker) : DbCommandInterceptor
{
    /// <summary>The longest a read is held if the test never releases it.</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed = 1;

    /// <summary>Completes when the matching read has been caught and is waiting.</summary>
    public Task Held => _held.Task;

    /// <summary>Lets the held read go on.</summary>
    public void Release() => _released.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains(marker, StringComparison.Ordinal)
            && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
        {
            _held.TrySetResult();
            await _released.Task.WaitAsync(MaxHold, CancellationToken.None)
                .ContinueWith(_ => { }, TaskScheduler.Default);
        }

        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
