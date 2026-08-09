using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Fails the first command matching a marker with a TRANSIENT fault, so EF's retrying execution
/// strategy re-runs the operation for real.
///
/// <para>
/// A bare <see cref="TimeoutException"/> is the injectable one. EF 10.0.1's
/// <c>SqlServerTransientExceptionDetector</c> treats it as transient — unlike a command timeout,
/// which arrives as <c>SqlException</c> with <c>Number == -2</c> and is deliberately NOT retried
/// (see the note on <c>EnableRetryOnFailure</c> in <c>ServiceCollectionExtensions</c>). Fabricating a
/// transient <c>SqlException</c> is not possible from test code: it has no public constructor.
/// </para>
/// <para>
/// One-shot by design. It disarms after firing so the retry proceeds normally; permanently armed, it
/// would prove only that the strategy eventually gives up.
/// </para>
/// </summary>
public sealed class TransientFailureInterceptor : DbCommandInterceptor
{
    private readonly string _marker;
    private int _fired;

    /// <param name="marker">Substring of the command text to fail on, e.g. "INSERT INTO [Transactions]".</param>
    public TransientFailureInterceptor(string marker) => _marker = marker;

    /// <summary>True once the transient fault has actually been injected.</summary>
    public bool Fired => Volatile.Read(ref _fired) == 1;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        FailOnce(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        FailOnce(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void FailOnce(DbCommand command)
    {
        if (!command.CommandText.Contains(_marker, StringComparison.Ordinal))
        {
            return;
        }

        // CompareExchange rather than read-then-write: "exactly once" is the property this fixture
        // sells, and a second injection would exhaust the retry budget and fail the test for a
        // reason that looks like the bug under test.
        if (Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
        {
            return;
        }

        throw new TimeoutException($"Injected transient fault on: {_marker}");
    }
}
