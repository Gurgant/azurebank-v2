using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Records, for every command a context sends, its text and the timeout it was sent with.
/// </summary>
/// <remarks>
/// <see cref="CommandRecordingInterceptor"/> keeps the text only. A command's timeout is set on the
/// command itself, from the context that built it, so this is the one place a test can read what a
/// deployment's statements actually run under.
/// </remarks>
public sealed class CommandTimeoutRecordingInterceptor : DbCommandInterceptor
{
    private readonly ConcurrentQueue<(string Text, int TimeoutSeconds)> _commands = new();

    /// <summary>Every command sent, in order.</summary>
    public IReadOnlyList<(string Text, int TimeoutSeconds)> Commands => [.. _commands];

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Enqueue((command.CommandText, command.CommandTimeout));
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Enqueue((command.CommandText, command.CommandTimeout));
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Enqueue((command.CommandText, command.CommandTimeout));
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}
