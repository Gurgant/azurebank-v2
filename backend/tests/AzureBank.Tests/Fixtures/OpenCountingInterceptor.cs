using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Counts the connections EF is about to open through the context it is attached to, so a test can
/// say "this command opened nothing" by reading the count rather than by trusting the code path.
/// </summary>
/// <remarks>
/// It counts the attempt, before SqlClient is asked: an open that then fails still counts. A test
/// that asserts zero needs a second test in which the same instrument counts at least one, or the
/// zero could be an interceptor that was never attached.
/// </remarks>
public sealed class OpenCountingInterceptor : DbConnectionInterceptor
{
    private int _opens;

    /// <summary>How many times EF started to open a connection.</summary>
    public int Opens => Volatile.Read(ref _opens);

    public override InterceptionResult ConnectionOpening(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        Interlocked.Increment(ref _opens);
        return base.ConnectionOpening(connection, eventData, result);
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _opens);
        return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
    }
}
