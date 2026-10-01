using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AzureBank.Seeder.Commands;

/// <summary>How the wait for the database ended.</summary>
public enum GateVerdict
{
    /// <summary>
    /// Go on to EF. An open succeeded; or, never on an Azure SQL name, the server answered
    /// something the wait leaves to EF.
    /// </summary>
    Proceed,

    /// <summary>The server did not accept a connection within the wait.</summary>
    TimedOut,

    /// <summary>The server refused the login three times in a row.</summary>
    LoginRefused,

    /// <summary>The server holds the database and this login cannot open it, three times in a row.</summary>
    CannotOpenDatabase,

    /// <summary>The server is an Azure SQL one and holds no database of that name.</summary>
    NoSuchDatabase,
}

/// <summary>What <c>master</c> said about the database a connection could not open.</summary>
public enum MasterAnswer
{
    /// <summary>The server holds no database of that name.</summary>
    NoRow,

    /// <summary>The server holds it and it is not online: restoring, recovering, starting.</summary>
    NotOnline,

    /// <summary>The server holds it and it is online.</summary>
    Online,

    /// <summary>The server stopped answering before <c>master</c> could be read.</summary>
    NotReachable,

    /// <summary>The server answered and would not let this login read <c>master</c>.</summary>
    Refused,
}

/// <summary>The wait's verdict and the last thing the server answered, for the line that reports it.</summary>
/// <param name="Verdict">How the wait ended.</param>
/// <param name="LastAnswer">
/// The number, the class and the first line of the last failed open's message, or the type alone of
/// a failure that is not SQL Server's; null when no open failed.
/// </param>
public readonly record struct GateResult(GateVerdict Verdict, string? LastAnswer);

/// <summary>
/// The wait <c>migrate</c> runs before it hands the database to EF: one open every two seconds
/// until the server answers or the time is up, and a Warning for every attempt that failed.
/// </summary>
/// <remarks>
/// <para>
/// WHY EF'S OWN RETRY IS NOT ENOUGH. A one-shot has nobody to start it again, and EF's migrator
/// opens its connection and takes its lock OUTSIDE the execution strategy. Measured 2026-10-01 on
/// the compose SQL Server: stopped, a run started, the server started 15 s later, and the run
/// failed on "TCP Provider, error: 35" (number 0, class 20) after one retry Warning. An open that
/// lands while the server comes back is exactly the case the retry budget was meant to cover.
/// </para>
/// <para>
/// WHY 4060 IS NOT HANDED TO EF. Seconds after it starts, a server answers 4060 ("cannot open
/// database") for a database it holds and has not brought online yet (7 of 158 opens across three
/// stop/start cycles). EF reads 4060 as "the database does not exist" and answers with CREATE
/// DATABASE. So on a 4060 the gate asks <c>master</c> what the server holds. A database that is
/// there and not online is waited for. One that is not there is EF's to create, EXCEPT on an Azure
/// SQL name, where a new database is a new paid resource at the service's default size: there the
/// run stops, and it also stops when <c>master</c> cannot be read, which is the case for the login
/// a deployment gives this job.
/// </para>
/// <para>
/// ON AN AZURE SQL NAME ONLY AN OPEN THAT SUCCEEDED HANDS THE DATABASE TO EF (ADR-0060). Off Azure
/// an answer the wait does not judge goes to EF at once, with EF's own strategy and message. On an
/// Azure SQL name that was a way round the paragraph above. Measured 2026-10-01 on LocalDB, with
/// the gate told the name was Azure's and its first open answered 40613 or 40197, class 17, for a
/// database the server did not hold: one open, and the run exited 0 with a new database, 16
/// migrations applied. Those are also what Azure SQL answers while a database is not available
/// right now: Microsoft's table of transient faults lists 40197 and 40613 at class 17 and 49918 at
/// 16 (read 2026-10-01; none was produced on Azure), so the wait did not cover them. There, every
/// failed open is now waited for, a failure that is not SQL Server's included. The price: an
/// answer that waiting cannot change takes the whole wait before the run ends.
/// </para>
/// <para>
/// WHY A REFUSED LOGIN IS COUNTED. EF retries 18456 every 500 ms for a minute and logs nothing:
/// 61.1 s and one line of output for a wrong password. A starting server can refuse a login it
/// accepts a moment later, so one refusal is waited for; three with nothing else between them
/// (about four seconds from a server that is answering) end the wait with a sentence.
/// </para>
/// <para>
/// A TIME BUDGET, NOT A COUNT: a refused connection fails in milliseconds and a name that does
/// not resolve in about 12 s, so a number of attempts would mean anything from a second to
/// minutes. Retrying a failed open is always safe: nothing was sent.
/// </para>
/// <para>
/// THE OPEN IS A PLAIN SqlConnection, handed in as a delegate. Through the context, EF logs an
/// Error for every failed open, and a run that waited and then succeeded would carry Error lines.
/// The delegates are also what lets the suite script a server's answers.
/// </para>
/// </remarks>
public static class DatabaseGate
{
    /// <summary>The pause between two attempts.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);

    /// <summary>The same answer this many times with nothing else in between ends the wait.</summary>
    private const int InARow = 3;

    private const int LoginFailed = 18456;
    private const int CannotOpenDatabase = 4060;
    private const int Timeout = -2;
    private const byte FatalClass = 20;

    /// <summary>
    /// Opens until the database can be handed to EF, a verdict ends the wait, or
    /// <paramref name="wait"/> is over. Cancellation throws.
    /// </summary>
    /// <param name="open">Opens one connection on the string the run will use, and closes it.</param>
    /// <param name="askMaster">Asks <c>master</c> for the state of the database the string names.</param>
    /// <param name="azureSql">
    /// Whether the server's name is an Azure SQL one: there the database goes to EF only after an
    /// open succeeded, and never when the server holds none of that name.
    /// </param>
    /// <param name="wait">How long to go on trying. Zero is one attempt.</param>
    /// <param name="clock">The clock the deadline and the pauses run on.</param>
    /// <param name="logger">Gets one Warning per failed attempt.</param>
    /// <param name="cancellationToken">Ends the wait with an <see cref="OperationCanceledException"/>.</param>
    public static async Task<GateResult> WaitAsync(
        Func<CancellationToken, Task> open,
        Func<CancellationToken, Task<MasterAnswer>> askMaster,
        bool azureSql,
        TimeSpan wait,
        TimeProvider clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        var loginRefusals = 0;
        var openRefusals = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Exception failure;
            try
            {
                await open(cancellationToken);
                return new GateResult(GateVerdict.Proceed, null);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failure = e;
            }

            // Null for a failure that is not an answer from SQL Server.
            var sql = failure as SqlException;
            var answer = Describe(failure);
            GateVerdict? ending = null;

            if (sql?.Number == LoginFailed)
            {
                openRefusals = 0;
                if (++loginRefusals >= InARow)
                {
                    ending = GateVerdict.LoginRefused;
                }
            }
            else if (sql?.Number == CannotOpenDatabase)
            {
                loginRefusals = 0;
                switch (await askMaster(cancellationToken))
                {
                    case MasterAnswer.NoRow:
                        return new GateResult(azureSql ? GateVerdict.NoSuchDatabase : GateVerdict.Proceed, answer);

                    case MasterAnswer.Refused when !azureSql:
                        // A login that lives in one database of a local server cannot read master.
                        return new GateResult(GateVerdict.Proceed, answer);

                    case MasterAnswer.Online:
                    case MasterAnswer.Refused:
                        if (++openRefusals >= InARow)
                        {
                            ending = GateVerdict.CannotOpenDatabase;
                        }

                        break;

                    default:
                        // Not online yet, or the server went away again: neither is a refusal.
                        openRefusals = 0;
                        break;
                }
            }
            else if (sql is not null && (sql.Class >= FatalClass || sql.Number == Timeout))
            {
                loginRefusals = 0;
                openRefusals = 0;
            }
            else if (!azureSql)
            {
                // An answer the wait does not judge, or a failure that is not SQL Server's. EF
                // meets the same one, and its own strategy and message apply.
                return new GateResult(GateVerdict.Proceed, sql is null ? null : answer);
            }
            else
            {
                // On an Azure SQL name it is waited for, like a server that did not answer: only
                // an open that succeeded hands the database to EF (the remarks above say why).
                loginRefusals = 0;
                openRefusals = 0;
            }

            var left = wait - clock.GetElapsedTime(started);
            if (left < TimeSpan.Zero)
            {
                left = TimeSpan.Zero;
            }

            var secondsLeft = (int)Math.Ceiling(left.TotalSeconds);
            if (sql is not null)
            {
                logger.LogWarning(
                    "Waiting for the database ({Number}, class {Class}): {Answer}. {SecondsLeft} s left.",
                    sql.Number,
                    sql.Class,
                    FirstLine(sql),
                    secondsLeft);
            }
            else
            {
                // The type and nothing else: nobody has checked what such a message can quote.
                logger.LogWarning(
                    "Waiting for the database ({Failure}, not an answer from SQL Server). {SecondsLeft} s left.",
                    failure.GetType().Name,
                    secondsLeft);
            }

            if (ending is { } verdict)
            {
                return new GateResult(verdict, answer);
            }

            if (left == TimeSpan.Zero)
            {
                return new GateResult(GateVerdict.TimedOut, answer);
            }

            await Task.Delay(left < Pause ? left : Pause, clock, cancellationToken);
        }
    }

    /// <summary>
    /// The real open: one plain connection on <paramref name="connectionString"/>, opened and
    /// closed. It shares the run's pool, because the string is the one EF opens with.
    /// </summary>
    public static Func<CancellationToken, Task> Open(string connectionString) =>
        async cancellationToken =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
        };

    /// <summary>
    /// The real question to <c>master</c>: the same server and login, the <c>master</c> database,
    /// no pooling (it is asked a handful of times at most), and <c>sys.databases.state_desc</c>
    /// for the database <paramref name="connectionString"/> names.
    /// </summary>
    public static Func<CancellationToken, Task<MasterAnswer>> AskMaster(string connectionString) =>
        async cancellationToken =>
        {
            var named = new SqlConnectionStringBuilder(connectionString);
            var database = named.InitialCatalog;
            var master = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "master",
                Pooling = false,
            };

            try
            {
                await using var connection = new SqlConnection(master.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT state_desc FROM sys.databases WHERE name = @name";
                command.Parameters.AddWithValue("@name", database);
                var state = await command.ExecuteScalarAsync(cancellationToken) as string;
                return state is null
                    ? MasterAnswer.NoRow
                    : string.Equals(state, "ONLINE", StringComparison.OrdinalIgnoreCase)
                        ? MasterAnswer.Online
                        : MasterAnswer.NotOnline;
            }
            catch (SqlException e) when (e.Class >= FatalClass || e.Number == Timeout)
            {
                return MasterAnswer.NotReachable;
            }
            catch (SqlException)
            {
                return MasterAnswer.Refused;
            }
        };

    private static string Describe(Exception failure) =>
        failure is SqlException sql
            ? $"{sql.Number}, class {sql.Class}: {FirstLine(sql)}"
            : $"{failure.GetType().Name}, not an answer from SQL Server";

    /// <summary>
    /// The first line of SqlClient's message, without its closing full stop. It can name the
    /// server, the database and the login; SqlClient puts no password in it.
    /// </summary>
    private static string FirstLine(SqlException failure)
    {
        var message = failure.Message;
        var end = message.IndexOfAny(['\r', '\n']);
        return (end >= 0 ? message[..end] : message).Trim().TrimEnd('.');
    }
}
