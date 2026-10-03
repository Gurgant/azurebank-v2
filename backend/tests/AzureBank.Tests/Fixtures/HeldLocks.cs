using Microsoft.Data.SqlClient;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Locks taken from a second connection and held, in a transaction that stays open until this is
/// disposed: what makes a statement of the code under test WAIT ON THE SERVER.
/// </summary>
/// <remarks>
/// <para>
/// Written for a run that is stopped while one of its statements is already with the database. A
/// stop that lands before a statement is sent ends it as a cancellation. One that lands after it
/// was sent is answered by the server, and SqlClient reports that answer as the database's own
/// error ("Operation cancelled by user"), not as a cancellation. Code that tells the two apart by
/// the exception's type sees a stop as a failure. An interceptor cannot make that error: it has
/// no public constructor, and what matters is that the statement really was on the server.
/// </para>
/// <para>
/// <see cref="AStatementWaitsAsync"/> asks the server which request waits behind this session, so
/// a test goes on when the statement IS waiting and not after a pause that is hoped to be long
/// enough. Reading that view needs VIEW SERVER STATE, which the owner of a LocalDB instance and
/// the administrator of the CI container both have.
/// </para>
/// </remarks>
internal sealed class HeldLocks : IAsyncDisposable
{
    /// <summary>The longest a test waits for a statement to come and wait behind the locks.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;

    private HeldLocks(SqlConnection connection, SqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>
    /// Runs <paramref name="statement"/> in a transaction that is left open, so the locks it took
    /// are held.
    /// </summary>
    public static async Task<HeldLocks> TakeAsync(string connectionString, string statement, params SqlParameter[] parameters)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = statement;
        command.Parameters.AddRange(parameters);
        await command.ExecuteScalarAsync();
        return new HeldLocks(connection, transaction);
    }

    /// <summary>Completes once a statement of another session is waiting behind these locks.</summary>
    /// <exception cref="InvalidOperationException">None came to wait: the test would prove nothing.</exception>
    public async Task AStatementWaitsAsync()
    {
        await using var waiting = _connection.CreateCommand();
        waiting.Transaction = _transaction;
        waiting.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @@SPID";

        var giveUp = DateTime.UtcNow + MaxWait;
        while (DateTime.UtcNow < giveUp)
        {
            if ((int)(await waiting.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new InvalidOperationException(
            $"ARRANGE: no statement came to wait behind the held locks within {MaxWait.TotalSeconds:0} s, so the test proves nothing.");
    }

    public async ValueTask DisposeAsync()
    {
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
