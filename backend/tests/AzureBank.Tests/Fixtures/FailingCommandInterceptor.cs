using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Fails chosen commands with a fault EF does NOT retry, to stand in for a statement the database
/// refuses every time it is sent: a row that cannot be deleted, a table that cannot be written.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="TransientFailureInterceptor"/>, for the other kind of fault. That one
/// throws a bare <see cref="TimeoutException"/>, which the retrying execution strategy re-runs; this
/// one throws <see cref="InvalidOperationException"/>, which it does not, so the failure reaches the
/// code under test once per command.
/// </para>
/// <para>
/// Two ways to choose a command. <see cref="OnText"/> matches the statement's text.
/// <see cref="OnDeleteNaming"/> matches a DELETE that names one of a set of ids, in its text or in
/// any parameter: it picks out the statements about one demo copy without depending on how those
/// statements are written (a parameter per id, an inlined literal, or a JSON array of ids).
/// </para>
/// </remarks>
public sealed class FailingCommandInterceptor : DbCommandInterceptor
{
    private readonly Func<DbCommand, bool> _chosen;
    private int _failures;

    private FailingCommandInterceptor(Func<DbCommand, bool> chosen) => _chosen = chosen;

    /// <summary>The sentence every injected failure carries.</summary>
    public const string Message = "Injected failure: the database refused this statement.";

    /// <summary>Commands this interceptor failed. 0 means the test proved nothing.</summary>
    public int Failures => Volatile.Read(ref _failures);

    /// <summary>Fails every command whose text contains every marker.</summary>
    public static FailingCommandInterceptor OnText(params string[] markers) =>
        new(command => markers.All(marker => command.CommandText.Contains(marker, StringComparison.Ordinal)));

    /// <summary>Fails every DELETE that names one of <paramref name="ids"/>.</summary>
    public static FailingCommandInterceptor OnDeleteNaming(IEnumerable<Guid> ids)
    {
        var wanted = ids.ToHashSet();
        var asText = wanted.Select(id => id.ToString("D")).ToArray();

        bool Names(string? text) =>
            text is not null && asText.Any(id => text.Contains(id, StringComparison.OrdinalIgnoreCase));

        return new FailingCommandInterceptor(command =>
            command.CommandText.Contains("DELETE", StringComparison.Ordinal)
            && (Names(command.CommandText)
                || command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value switch
                {
                    Guid id => wanted.Contains(id),
                    string text => Names(text),
                    _ => false,
                })));
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        FailIfChosen(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        FailIfChosen(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void FailIfChosen(DbCommand command)
    {
        if (!_chosen(command))
        {
            return;
        }

        Interlocked.Increment(ref _failures);
        throw new InvalidOperationException(Message);
    }
}
