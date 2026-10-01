extern alias seeder;

using System.Reflection;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using DatabaseGate = seeder::AzureBank.Seeder.Commands.DatabaseGate;
using GateResult = seeder::AzureBank.Seeder.Commands.GateResult;
using GateVerdict = seeder::AzureBank.Seeder.Commands.GateVerdict;
using MasterAnswer = seeder::AzureBank.Seeder.Commands.MasterAnswer;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// The wait <c>migrate</c> runs before it hands the database to EF: one open every two seconds
/// until the server answers or the time is up, one Warning per attempt that failed, and a verdict.
/// </summary>
/// <remarks>
/// <para>
/// WHY IT EXISTS. EF's migrator opens its connection outside the execution strategy, so an open
/// that lands while SQL Server is coming back is not retried: measured 2026-10-01 against the
/// compose SQL Server, stopped and started 15 s into a run, the run failed on "TCP Provider, error:
/// 35" (number 0, class 20) after one retry Warning. Seconds later a recovering server answers 4060
/// for a database it has not brought online, which EF reads as "does not exist" and answers with
/// CREATE DATABASE. And a refused login is retried by EF every 500 ms for a minute with nothing
/// logged: 61.1 s and one line of output.
/// </para>
/// <para>
/// The gate is handed two delegates, the open and a question to <c>master</c>, so every case here
/// is a script of what the server answered. Time is a <see cref="FakeTimeProvider"/> that moves
/// itself forward whenever the gate starts a pause, so a test takes no real time and can read the
/// pauses the gate asked for.
/// </para>
/// </remarks>
public class DatabaseGateTests
{
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Minute = TimeSpan.FromSeconds(60);

    /// <summary>One run of the gate over a script: each open takes the next step.</summary>
    private sealed class Script
    {
        private readonly Queue<Exception?> _opens = new();
        private readonly Queue<MasterAnswer> _master = new();

        public SelfAdvancingClock Clock { get; } = new();

        public RecordingLoggerProvider Log { get; } = new();

        public int Opens { get; private set; }

        public int MasterQuestions { get; private set; }

        /// <summary>The open fails with <paramref name="failure"/>; null is an open that succeeds.</summary>
        public Script Then(Exception? failure, MasterAnswer? master = null)
        {
            _opens.Enqueue(failure);
            if (master is { } answer)
            {
                _master.Enqueue(answer);
            }

            return this;
        }

        public Script ThenSuccess() => Then(null);

        public Task<GateResult> Run(TimeSpan wait, bool azureSql = false, CancellationToken token = default) =>
            DatabaseGate.WaitAsync(
                _ =>
                {
                    Opens++;
                    // Past the end of the script the last step repeats: "it keeps failing this way".
                    var failure = _opens.Count > 1 ? _opens.Dequeue() : _opens.Peek();
                    return failure is null ? Task.CompletedTask : Task.FromException(failure);
                },
                _ =>
                {
                    MasterQuestions++;
                    return Task.FromResult(_master.Count > 1 ? _master.Dequeue() : _master.Peek());
                },
                azureSql,
                wait,
                Clock,
                Log.CreateLogger("gate"),
                token);

        public IReadOnlyList<string> Warnings =>
            Log.Lines.Where(line => line.Level == LogLevel.Warning).Select(line => line.Message).ToList();
    }

    /// <summary>
    /// A fake clock that moves forward by a pause's length as soon as the pause starts, from
    /// another thread, and keeps what was asked for.
    /// </summary>
    private sealed class SelfAdvancingClock : FakeTimeProvider
    {
        private readonly List<TimeSpan> _pauses = [];

        public IReadOnlyList<TimeSpan> Pauses
        {
            get
            {
                lock (_pauses)
                {
                    return _pauses.ToArray();
                }
            }
        }

        public DateTimeOffset StartedAt { get; }

        public SelfAdvancingClock() => StartedAt = GetUtcNow();

        public TimeSpan Elapsed => GetUtcNow() - StartedAt;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            lock (_pauses)
            {
                _pauses.Add(dueTime);
            }

            ThreadPool.QueueUserWorkItem(_ => Advance(dueTime));
            return timer;
        }
    }

    [Fact]
    public async Task AnOpenThatSucceeds_Proceeds_AtOnce()
    {
        var script = new Script().ThenSuccess();

        var result = await script.Run(Minute);

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.Proceed);
        script.Opens.Should().Be(1);
        script.Clock.Pauses.Should().BeEmpty();
        script.Warnings.Should().BeEmpty();
    }

    [Theory]
    // What an open raised while the compose SQL Server was stopped and started (2026-10-01): a name
    // that does not resolve, a connect timeout, a connection the server closed mid-login.
    [InlineData(11001, 20)]
    [InlineData(258, 20)]
    [InlineData(0, 20)]
    // A timeout SqlClient reports as -2, whatever its class.
    [InlineData(-2, 11)]
    public async Task AServerThatDoesNotAnswer_IsWaitedFor_OnePauseAndOneWarningPerFailure(int number, byte errorClass)
    {
        var script = new Script()
            .Then(Sql(number, errorClass))
            .Then(Sql(number, errorClass))
            .Then(Sql(number, errorClass))
            .ThenSuccess();

        var result = await script.Run(Minute);

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.Proceed);
        script.Opens.Should().Be(4);
        script.Clock.Pauses.Should().Equal(Pause, Pause, Pause);
        script.Warnings.Should().HaveCount(3).And.OnlyContain(
            warning => warning.StartsWith($"Waiting for the database ({number}, class {errorClass}): ", StringComparison.Ordinal));
        script.MasterQuestions.Should().Be(0, "master is asked only about a database that could not be opened");
    }

    [Fact]
    public async Task EveryWarning_SaysWhatTheServerAnswered_AndHowLongIsLeft()
    {
        // The first line only: SqlClient's text for a failed open runs to several, and the first
        // holds what failed. It can name the server, the database and the login, never a password.
        var script = new Script()
            .Then(Sql(11001, 20, "A network-related or instance-specific error occurred.\r\nSecond line, not for the log."))
            .Then(Sql(18456, 14, "Login failed for user 'migrator'."))
            .ThenSuccess();

        var result = await script.Run(Minute);

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.Proceed);
        script.Warnings.Should().Equal(
            "Waiting for the database (11001, class 20): A network-related or instance-specific error occurred. 60 s left.",
            "Waiting for the database (18456, class 14): Login failed for user 'migrator'. 58 s left.");
    }

    [Fact]
    public async Task AServerThatNeverAnswers_TimesOut_AfterTheWholeWait()
    {
        var script = new Script().Then(Sql(11001, 20, "No such host is known."));

        var result = await script.Run(TimeSpan.FromSeconds(10));

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.TimedOut);
        result.LastAnswer.Should().Be("11001, class 20: No such host is known");
        script.Clock.Elapsed.Should().Be(TimeSpan.FromSeconds(10), "the wait is a time budget, not a count of attempts");
        script.Opens.Should().Be(6, "one attempt at the start and one after each of five pauses, the last at the deadline");
        script.Warnings.Should().HaveCount(6, "the attempt that ran out of time is logged too");
    }

    [Fact]
    public async Task AWaitThatIsNotAMultipleOfThePause_EndsWithAShorterPause_NotALaterDeadline()
    {
        var script = new Script().Then(Sql(258, 20));

        var result = await script.Run(TimeSpan.FromSeconds(5));

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.TimedOut);
        script.Clock.Pauses.Should().Equal(Pause, Pause, TimeSpan.FromSeconds(1));
        script.Clock.Elapsed.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ARefusedLogin_EndsTheWait_TheThirdTimeInARow()
    {
        // EF retries a refused login for a minute and logs nothing. Three in a row is about four
        // seconds of the same refusal from a server that is answering.
        var script = new Script().Then(Sql(18456, 14, "Login failed for user 'migrator'."));

        var result = await script.Run(Minute);

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.LoginRefused);
        result.LastAnswer.Should().Contain("18456");
        script.Opens.Should().Be(3);
        script.Clock.Pauses.Should().Equal(Pause, Pause);
        script.Warnings.Should().HaveCount(3);
    }

    [Fact]
    public async Task ARefusedLogin_BetweenOtherAnswers_IsStillWaitedFor()
    {
        // A server that is starting can refuse a login it will accept a moment later. Only three
        // refusals with nothing else in between end the wait.
        var script = new Script()
            .Then(Sql(18456, 14))
            .Then(Sql(18456, 14))
            .Then(Sql(11001, 20))
            .Then(Sql(18456, 14))
            .Then(Sql(18456, 14))
            .ThenSuccess();

        var result = await script.Run(Minute);

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.Proceed);
        script.Opens.Should().Be(6);
        script.Clock.Pauses.Should().HaveCount(5);
    }

    [Fact]
    public async Task ADatabaseThatIsNotOnlineYet_IsWaitedFor()
    {
        // A recovering server answers 4060 for a database it holds and has not brought online.
        // Handing that to EF would have it send CREATE DATABASE.
        var script = new Script()
            .Then(Sql(4060, 11), MasterAnswer.NotOnline)
            .Then(Sql(4060, 11), MasterAnswer.NotOnline)
            .ThenSuccess();

        var result = await script.Run(Minute);

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.Proceed);
        script.Opens.Should().Be(3);
        script.MasterQuestions.Should().Be(2);
        script.Clock.Pauses.Should().Equal(Pause, Pause);
        script.Warnings.Should().HaveCount(2).And.OnlyContain(warning => warning.Contains("(4060, class 11)"));
    }

    [Fact]
    public async Task ADatabaseTheServerDoesNotHold_Proceeds_OffAzure_SoMigrateCreatesIt()
    {
        // compose, CI and LocalDB start from a server with no database: MigrateAsync creates it.
        var script = new Script().Then(Sql(4060, 11), MasterAnswer.NoRow);

        var result = await script.Run(Minute, azureSql: false);

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.Proceed);
        script.Opens.Should().Be(1);
        script.Clock.Pauses.Should().BeEmpty();
    }

    [Fact]
    public async Task ADatabaseTheServerDoesNotHold_IsRefused_OnAnAzureSqlName_AtOnce()
    {
        var script = new Script().Then(Sql(4060, 11), MasterAnswer.NoRow);

        var result = await script.Run(Minute, azureSql: true);

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.NoSuchDatabase);
        script.Opens.Should().Be(1);
        script.Clock.Pauses.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADatabaseThatIsOnline_AndStillCannotBeOpened_EndsTheWait_TheThirdTimeInARow(bool azureSql)
    {
        // The database is there and this login may not use it. EF would read the 4060 as "does not
        // exist", send CREATE DATABASE, and fail on a name that is taken.
        var script = new Script().Then(Sql(4060, 11), MasterAnswer.Online);

        var result = await script.Run(Minute, azureSql);

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.CannotOpenDatabase);
        script.Opens.Should().Be(3);
        script.Clock.Pauses.Should().Equal(Pause, Pause);
    }

    [Fact]
    public async Task WhenMasterCannotBeReached_TheWaitGoesOn()
    {
        var script = new Script()
            .Then(Sql(4060, 11), MasterAnswer.NotReachable)
            .ThenSuccess();

        var result = await script.Run(Minute);

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.Proceed);
        script.Opens.Should().Be(2);
        script.Clock.Pauses.Should().Equal(Pause);
    }

    [Fact]
    public async Task WhenMasterRefusesTheLogin_OffAzure_ItProceeds()
    {
        // A login that lives in one database of a local server cannot read master. EF then decides,
        // as it did before the gate existed.
        var script = new Script().Then(Sql(4060, 11), MasterAnswer.Refused);

        var result = await script.Run(Minute, azureSql: false);

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.Proceed);
        script.Opens.Should().Be(1);
    }

    [Fact]
    public async Task WhenMasterRefusesTheLogin_OnAnAzureSqlName_NothingReachesEf()
    {
        // The deployment's migrate login is a user of one database and cannot read master. A 4060
        // there never becomes CREATE DATABASE: the wait ends after three.
        var script = new Script().Then(Sql(4060, 11), MasterAnswer.Refused);

        var result = await script.Run(Minute, azureSql: true);

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.CannotOpenDatabase);
        script.Opens.Should().Be(3);
        script.Clock.Pauses.Should().Equal(Pause, Pause);
    }

    [Fact]
    public async Task AnAnswerTheGateDoesNotKnow_Proceeds_AtOnce()
    {
        // 262: CREATE DATABASE permission denied. Not the gate's to judge: EF's own strategy and
        // message apply.
        var script = new Script().Then(Sql(262, 14));

        var result = await script.Run(Minute);

        using var all = new AssertionScope();
        result.Verdict.Should().Be(GateVerdict.Proceed);
        script.Opens.Should().Be(1);
        script.Clock.Pauses.Should().BeEmpty();
        script.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailureThatIsNotSqlServers_Proceeds_AtOnce()
    {
        var script = new Script().Then(new InvalidOperationException("not a SqlException"));

        var result = await script.Run(Minute);

        result.Verdict.Should().Be(GateVerdict.Proceed);
        script.Opens.Should().Be(1);
    }

    [Fact]
    public async Task WithNoWait_ThereIsOneAttempt_AndNoPause()
    {
        var failing = new Script().Then(Sql(11001, 20));
        var succeeding = new Script().ThenSuccess();

        var failed = await failing.Run(TimeSpan.Zero);
        var succeeded = await succeeding.Run(TimeSpan.Zero);

        using var all = new AssertionScope();
        failed.Verdict.Should().Be(GateVerdict.TimedOut);
        failing.Opens.Should().Be(1);
        failing.Clock.Pauses.Should().BeEmpty();
        failing.Warnings.Should().ContainSingle().Which.Should().EndWith("0 s left.");
        succeeded.Verdict.Should().Be(GateVerdict.Proceed);
    }

    [Fact]
    public async Task ACancelledWait_Throws_RatherThanReturningAVerdict()
    {
        // The command turns the cancellation into its own line and exit 1. A verdict here would
        // read as "the database did not answer", which is not what happened.
        using var cancelled = new CancellationTokenSource();
        var script = new Script().Then(Sql(11001, 20));
        await cancelled.CancelAsync();

        var run = () => script.Run(Minute, token: cancelled.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>A <see cref="SqlException"/> carrying one error of the given number and class.</summary>
    private static SqlException Sql(int number, byte errorClass, string? message = null)
    {
        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        var add = typeof(SqlErrorCollection).GetMethod("Add", Any)!;
        var ctor = typeof(SqlError).GetConstructor(
            Any, [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception)])!;
        add.Invoke(collection, [ctor.Invoke([number, (byte)0, errorClass, "server", message ?? $"error {number}", "", 0, null])]);

        var create = typeof(SqlException).GetMethod(
            "CreateException", Any, [typeof(SqlErrorCollection), typeof(string)])!;
        return (SqlException)create.Invoke(null, [collection, "16.0"])!;
    }
}
