using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Makes the first command that matches wait on the SERVER: it prepends <c>WAITFOR DELAY</c> to the
/// command's own text, so the statement is sent, SQL Server sits on it, and the only way to end the
/// wait early is SqlClient cancelling the command — the path a request deadline or an abandoned
/// request has to take against a database that has stopped answering.
/// </summary>
/// <remarks>
/// <para>
/// Server-side on purpose. A delay in the interceptor itself (as <see cref="HoldingReadInterceptor"/>
/// and <see cref="SlowAuditTailInterceptor"/> do) holds the request in .NET, where a cancelled token
/// ends it without SqlClient ever sending an attention; that would prove nothing about the command
/// timeout, the attention or what EF surfaces when a running command is cancelled.
/// </para>
/// <para>
/// One-shot, and signalled: <see cref="Fired"/> says a command was actually delayed, so a test that
/// reports on a run where nothing matched fails instead of passing. Keep the delay under the
/// command timeout (30 s): past it the command ends in its own timeout, <c>-2</c>, whatever the test
/// meant to observe.
/// </para>
/// </remarks>
public sealed class WaitForInterceptor : DbCommandInterceptor
{
    private readonly Func<string, bool> _matches;
    private readonly string _prefix;
    private int _armed = 1;

    /// <param name="matches">Whether a command's text is the one to delay.</param>
    /// <param name="delay">How long SQL Server waits before running it; whole seconds, under 30.</param>
    public WaitForInterceptor(Func<string, bool> matches, TimeSpan delay)
    {
        _matches = matches;
        _prefix = "WAITFOR DELAY '" + delay.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) + "';" + Environment.NewLine;
    }

    /// <summary>Delays the first command whose text contains every one of <paramref name="markers"/>.</summary>
    public static WaitForInterceptor OnCommandContaining(TimeSpan delay, params string[] markers) =>
        new(text => markers.All(marker => text.Contains(marker, StringComparison.Ordinal)), delay);

    /// <summary>True once a command has been sent with the delay in front of it.</summary>
    public bool Fired => Volatile.Read(ref _armed) == 0;

    private void DelayIfMatching(DbCommand command)
    {
        if (!_matches(command.CommandText) || Interlocked.CompareExchange(ref _armed, 0, 1) != 1)
        {
            return;
        }

        command.CommandText = _prefix + command.CommandText;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        DelayIfMatching(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        DelayIfMatching(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        DelayIfMatching(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        DelayIfMatching(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        DelayIfMatching(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        DelayIfMatching(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}
