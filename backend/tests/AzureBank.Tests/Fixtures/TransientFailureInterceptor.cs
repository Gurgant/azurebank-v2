using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Fails the first command matching a marker with a TRANSIENT fault, so EF's retrying execution
/// strategy re-runs the operation for real.
///
/// <para>
/// A bare <see cref="TimeoutException"/> is the fault unless the test gives another. EF 10.0.1's
/// <c>SqlServerTransientExceptionDetector</c> treats it as transient — unlike a command timeout,
/// which arrives as <c>SqlException</c> with <c>Number == -2</c> and is deliberately NOT retried
/// (see the note on <c>EnableRetryOnFailure</c> in <c>ServiceCollectionExtensions</c>).
/// </para>
/// <para>
/// A test about one error of SQL Server's gives that error as the fault. <c>SqlException</c> has no
/// public constructor, and one can be built all the same, as SqlClient builds its own:
/// <see cref="SqlErrors"/> does, for the number and the class the test names. (Until 2026-10-04
/// this said that could not be done from test code; two unit test classes already did it.)
/// </para>
/// <para>
/// One-shot by design. It disarms after firing so the retry proceeds normally; permanently armed, it
/// would prove only that the strategy eventually gives up.
/// </para>
/// </summary>
public sealed class TransientFailureInterceptor : DbCommandInterceptor
{
    private readonly string _marker;
    private readonly Func<Exception> _fault;
    private int _fired;

    /// <param name="marker">Substring of the command text to fail on, e.g. "INSERT INTO [Transactions]".</param>
    /// <param name="fault">Makes what is thrown; a bare <see cref="TimeoutException"/> when not given.</param>
    public TransientFailureInterceptor(string marker, Func<Exception>? fault = null)
    {
        _marker = marker;
        _fault = fault ?? (() => new TimeoutException($"Injected transient fault on: {marker}"));
    }

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

        throw _fault();
    }
}
