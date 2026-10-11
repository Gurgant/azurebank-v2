using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Makes every save of one account lose the race for its <c>RowVersion</c> while armed: before
/// each command that updates <c>[Accounts]</c>, it writes that account's row from a second
/// connection.
/// </summary>
/// <remarks>
/// <para>
/// <c>RowVersion</c> is a SQL Server <c>rowversion</c> column, so any UPDATE of the row moves it,
/// including this one, which sets <c>UpdatedAt</c> to the value it already has: the balance and
/// every other column stay as they are. The intercepted UPDATE carries
/// <c>WHERE RowVersion = </c> the value its attempt read, matches zero rows, and EF raises
/// <c>DbUpdateConcurrencyException</c>. Armed for a whole request, it does that to every attempt
/// the request makes.
/// </para>
/// <para>
/// WITH <c>closingOn</c>, ONE OF THOSE WRITES IS A CLOSURE: the write of that number closes the
/// account, with the statement <see cref="OutOfBandClosureInterceptor"/> uses, and the save it
/// rides loses to it as it loses to the others. That places a closure on the attempt a proof
/// names, the last one included.
/// </para>
/// <para>
/// WITH <c>saves</c>, ONLY THAT MANY SAVES LOSE: the next one meets the RowVersion it read, so a
/// proof can let the write through on the attempt it names.
/// </para>
/// <para>
/// A raw connection, because every context the factory builds carries this interceptor. It cannot
/// deadlock with the save it intercepts: a deposit's transaction holds the audit tail's lock and
/// has not touched the account row when its UPDATE is sent, and this statement commits by itself
/// before that UPDATE runs.
/// </para>
/// </remarks>
public sealed class LostRowVersionRaceInterceptor(string connectionString) : DbCommandInterceptor
{
    private Guid? _accountId;
    private int? _closingOn;
    private int? _saves;
    private int _fired;

    /// <summary>
    /// Starts losing the race for <paramref name="accountId"/>. With <paramref name="closingOn"/>,
    /// the write of that number, counted from one, closes the account. With
    /// <paramref name="saves"/>, only that many saves lose.
    /// </summary>
    public void Arm(Guid accountId, int? closingOn = null, int? saves = null)
    {
        _closingOn = closingOn;
        _saves = saves;
        _accountId = accountId;
    }

    /// <summary>Stops: the next save of the account meets the RowVersion it read.</summary>
    public void Disarm() => _accountId = null;

    /// <summary>How many saves of the account were made to lose the race.</summary>
    public int Fired => Volatile.Read(ref _fired);

    /// <summary>True once the write that closes the account matched its open row.</summary>
    public bool Closed { get; private set; }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await MoveTheRowVersionAsync(command, cancellationToken);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await MoveTheRowVersionAsync(command, cancellationToken);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private async Task MoveTheRowVersionAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (_accountId is not { } accountId
            || !command.CommandText.Contains("UPDATE [Accounts]", StringComparison.Ordinal)
            || Fired >= _saves)
        {
            return;
        }

        if (_closingOn == Fired + 1)
        {
            Closed = await OutOfBandClosureInterceptor.CloseAsync(
                connectionString, accountId, cancellationToken: cancellationToken) == 1;
            Interlocked.Increment(ref _fired);
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var touch = connection.CreateCommand();
        touch.CommandText = "UPDATE [Accounts] SET [UpdatedAt] = [UpdatedAt] WHERE [Id] = @id";
        touch.Parameters.Add(new SqlParameter("@id", accountId));
        if (await touch.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            Interlocked.Increment(ref _fired);
        }
    }
}
