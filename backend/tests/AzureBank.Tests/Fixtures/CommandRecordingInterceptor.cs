using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Records the text of every command EF sends while it is recording, so a test can say what a
/// request wrote by reading what reached the server rather than what the code was meant to do.
/// </summary>
/// <remarks>
/// <para>
/// Written for 06 §10 O0-2 item 2: "renewal writes nothing" is a claim about the wire, and the
/// only instrument that sees the wire from inside the test host is a command interceptor. It sees
/// commands only, and only those sent through the <c>AzureBankDbContext</c> this factory builds:
/// on the InMemory provider there are none, so the claim is tested on SQL Server.
/// </para>
/// <para>
/// <see cref="Selects"/> is the half that makes an empty <see cref="Writes"/> mean something. A
/// recorder that never matched would report zero writes forever; one that saw the request's own
/// reads was plainly listening while the writes did not come.
/// </para>
/// <para>
/// A command counts as a read only when its text starts with <c>SELECT</c>. EF's SQL Server
/// batches for tracked changes start with <c>SET IMPLICIT_TRANSACTIONS OFF; SET NOCOUNT ON;</c>
/// and a set-based update with <c>UPDATE</c>, so both land in <see cref="Writes"/>, as does
/// anything else that is not plainly a read.
/// </para>
/// </remarks>
public sealed class CommandRecordingInterceptor : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();
    private int _recording;

    /// <summary>Starts recording. Commands sent before this call are not kept.</summary>
    public void Start() => Volatile.Write(ref _recording, 1);

    /// <summary>Stops recording; what was recorded stays readable.</summary>
    public void Stop() => Volatile.Write(ref _recording, 0);

    /// <summary>Every recorded command whose text starts with <c>SELECT</c>.</summary>
    public IReadOnlyList<string> Selects => _commands.Where(IsSelect).ToList();

    /// <summary>Every recorded command that is not a plain <c>SELECT</c>.</summary>
    public IReadOnlyList<string> Writes => _commands.Where(c => !IsSelect(c)).ToList();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Record(DbCommand command)
    {
        if (Volatile.Read(ref _recording) == 1)
        {
            _commands.Enqueue(command.CommandText);
        }
    }

    private static bool IsSelect(string commandText) =>
        commandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
}
