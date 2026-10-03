extern alias seeder;

using System.CommandLine;
using System.Data.Common;
using AzureBank.Shared.Entities;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using GateResult = seeder::AzureBank.Seeder.Commands.GateResult;
using GateVerdict = seeder::AzureBank.Seeder.Commands.GateVerdict;
using MigrateCommand = seeder::AzureBank.Seeder.Commands.MigrateCommand;
using RecycleCommand = seeder::AzureBank.Seeder.Commands.RecycleCommand;
using ResetCommand = seeder::AzureBank.Seeder.Commands.ResetCommand;
using RunCancellation = seeder::AzureBank.Seeder.Seeders.RunCancellation;
using SeedCommand = seeder::AzureBank.Seeder.Commands.SeedCommand;
using SeedPoolCommand = seeder::AzureBank.Seeder.Commands.SeedPoolCommand;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// What the Seeder's commands refuse, and that they refuse it before any connection is opened:
/// exit code 2 and one sentence. Run on the tool's own composition root, with an interceptor that
/// counts the opens EF starts.
/// </summary>
/// <remarks>
/// <para>
/// Before, the PIN pepper was checked in <c>Program.cs</c>, ahead of the command line, so a missing
/// one ended every invocation (<c>--help</c> included) with an unhandled exception; with no
/// connection string the tool fell back to <c>Server=localhost</c>; and <c>seed</c> and
/// <c>reset</c> opened whatever server the string named.
/// </para>
/// <para>
/// A test that would open a connection if its guard regressed names a server nobody can register
/// (an underscore is not allowed in an Azure SQL server name), one second to connect and no EF
/// retry, so a regression fails fast instead of reaching anything.
/// </para>
/// <para>
/// ZERO OPENS IS ONLY EVIDENCE BESIDE A COUNT THAT IS NOT ZERO.
/// <see cref="Seed_OnAServerThatIsNotThere_Fails_AndTheOpenIsCounted"/> is that count: the same
/// instrument, the same registration, and a command that does reach for the database.
/// </para>
/// </remarks>
public class SeederCommandTests
{
    private const string ConnectionKey = "ConnectionStrings:DefaultConnection";
    private const string PepperKey = "Security:PinPepper";
    private const string DemoKey = "Demo:Enabled";

    private const string AzureName = "not_a_server.database.windows.net";

    private const string AzureConnection =
        "Server=tcp:" + AzureName + ",1433;Database=x;User Id=u;Password=not-a-secret;Connect Timeout=1";

    // Port 1 on loopback: nothing listens there, and nothing leaves the machine.
    private const string AbsentServer =
        "Server=127.0.0.1,1;Database=x;User Id=u;Password=not-a-secret;Connect Timeout=1";

    // A server and no database. SqlClient would open the login's default database.
    private const string NoDatabase =
        "Server=127.0.0.1,1;User Id=u;Password=not-a-secret;Connect Timeout=1";

    private static readonly (string Key, string? Value) NoEfRetry = ("Database:MaxRetryCount", "0");

    private static Task<int> Run(string command, ServiceProvider provider, CancellationToken token = default) =>
        command switch
        {
            "migrate" => MigrateCommand.RunAsync(provider, TimeSpan.Zero, token),
            "seed" => SeedCommand.RunAsync(provider, token),
            "reset" => ResetCommand.RunAsync(provider, confirm: true, token),
            "seed-pool" => SeedPoolCommand.RunAsync(provider, copies: null, token),
            "recycle" => RecycleCommand.RunAsync(provider, token),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "not a Seeder command"),
        };

    /// <summary>
    /// The demo flag a command runs with when a test is about something else: on for the two pool
    /// commands, off for the others, so that each gets past its own refusal of the flag and the
    /// refusal the test is about is the one left.
    /// </summary>
    private static (string Key, string? Value) TheFlagItRunsWith(string command) =>
        (DemoKey, command is "seed-pool" or "recycle" ? "true" : "false");

    private static IEnumerable<string> Errors(RecordingLoggerProvider log) =>
        log.Lines.Where(line => line.Level == LogLevel.Error).Select(line => line.Message);

    [Theory]
    [InlineData("seed", "seed adds four users whose password and PIN are public")]
    [InlineData("reset", "reset drops the database and creates it again")]
    public async Task OnAnAzureSqlName_SeedAndReset_AreRefused_AndNothingIsOpened(string command, string reason)
    {
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        await using var provider = SeederHost.Build(
            log, opens, onCommittedSettings: false, (ConnectionKey, AzureConnection), (PepperKey, SeederHost.Pepper), NoEfRetry);

        var exitCode = await Run(command, provider);

        using var all = new AssertionScope();
        exitCode.Should().Be(2, "refused before any connection was opened");
        Errors(log).Should().ContainSingle()
            .Which.Should().Contain($"{command} refused: {AzureName} is an Azure SQL server")
            .And.Contain(reason)
            .And.Contain("Nothing was opened");
        opens.Opens.Should().Be(0);
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("reset")]
    [InlineData("seed-pool")]
    [InlineData("recycle")]
    public async Task WithoutAPinPepper_EveryCommandThatWritesAPin_IsRefused_AndNothingIsOpened(string command)
    {
        // The pool's two commands write PIN hashes too: recycle tops the pool up before it deletes.
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        await using var provider = SeederHost.Build(
            log, opens, onCommittedSettings: false, (ConnectionKey, AbsentServer), NoEfRetry, TheFlagItRunsWith(command));

        var exitCode = await Run(command, provider);

        using var all = new AssertionScope();
        exitCode.Should().Be(2);
        Errors(log).Should().ContainSingle()
            .Which.Should().Contain($"{command} refused")
            .And.Contain("Security:PinPepper must be configured with at least 32 characters")
            .And.NotContain("   at ", "the refusal is a sentence, not a stack trace");
        opens.Opens.Should().Be(0, "reset must not drop a database it cannot then seed");
    }

    [Theory]
    [InlineData("migrate")]
    [InlineData("seed")]
    [InlineData("reset")]
    [InlineData("seed-pool")]
    [InlineData("recycle")]
    public async Task WithoutAConnectionString_EveryCommand_IsRefused_AndNothingIsOpened(string command)
    {
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        await using var provider = SeederHost.Build(
            log, opens, onCommittedSettings: false, (PepperKey, SeederHost.Pepper), NoEfRetry, TheFlagItRunsWith(command));

        var exitCode = await Run(command, provider);

        using var all = new AssertionScope();
        exitCode.Should().Be(2);
        Errors(log).Should().ContainSingle()
            .Which.Should().Contain($"{command} refused")
            .And.Contain("ConnectionStrings__DefaultConnection", "the sentence names the variable to set");
        opens.Opens.Should().Be(0, "there is no fallback to a server on localhost");
    }

    [Theory]
    [InlineData("migrate")]
    [InlineData("seed")]
    [InlineData("reset")]
    [InlineData("seed-pool")]
    [InlineData("recycle")]
    public async Task AStringTheParserRefuses_IsRefused_AndNoneOfItsTextIsPrinted(string command)
    {
        // A password holding an unquoted ';' leaves a tail the parser reads as a keyword and names
        // in its message: "Keyword not supported: 'fragment'". That tail is half a password.
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        await using var provider = SeederHost.Build(
            log,
            opens,
            onCommittedSettings: false,
            (ConnectionKey, "Server=127.0.0.1,1;Database=x;User Id=u;Password=aaa;FRAGMENT=bbb;Connect Timeout=1"),
            (PepperKey, SeederHost.Pepper),
            NoEfRetry,
            TheFlagItRunsWith(command));

        var exitCode = await Run(command, provider);

        var output = string.Join('\n', log.Lines.Select(line => line.Message));
        using var all = new AssertionScope();
        exitCode.Should().Be(2);
        Errors(log).Should().ContainSingle()
            .Which.Should().Contain($"{command} refused")
            .And.Contain("is not a SQL Server connection string this tool can read");
        output.Should().NotContainEquivalentOf("fragment");
        output.Should().NotContain("bbb");
        output.Should().NotContain("aaa");
        opens.Opens.Should().Be(0);
    }

    [Theory]
    [InlineData("migrate")]
    [InlineData("seed")]
    [InlineData("reset")]
    [InlineData("seed-pool")]
    [InlineData("recycle")]
    public async Task AStringThatNamesNoDatabase_IsRefused_AndNothingIsOpened(string command)
    {
        // Without a database in the string the server picks the login's default one, and migrate
        // would build the schema there. Before, each command went on to open the server.
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        await using var provider = SeederHost.Build(
            log,
            opens,
            onCommittedSettings: false,
            (ConnectionKey, NoDatabase),
            (PepperKey, SeederHost.Pepper),
            NoEfRetry,
            TheFlagItRunsWith(command));

        var exitCode = await Run(command, provider);

        using var all = new AssertionScope();
        exitCode.Should().Be(2, "refused before any connection was opened");
        Errors(log).Should().ContainSingle()
            .Which.Should().Contain($"{command} refused")
            .And.Contain("names no database")
            .And.Contain("Nothing was opened");
        opens.Opens.Should().Be(0);
    }

    [Theory]
    // The keyword under its two names, then the setting with a string that leaves it unset.
    [InlineData("Connect Timeout=0", null)]
    [InlineData("Connection Timeout=0", null)]
    [InlineData(null, "0")]
    public async Task Migrate_WithAConnectTimeoutOfZero_IsRefused_BeforeTheWait(string? keyword, string? setting)
    {
        // SqlClient reads 0 as "no limit": one open then never ends, and the wait is only read
        // between two opens. The wait is handed in, so a regression is seen and nothing hangs.
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        var settings = new List<(string Key, string? Value)>
        {
            (ConnectionKey, "Server=127.0.0.1,1;Database=x;User Id=u;Password=not-a-secret" + (keyword is null ? "" : ";" + keyword)),
            NoEfRetry,
        };
        if (setting is not null)
        {
            settings.Add(("Database:ConnectTimeoutSeconds", setting));
        }

        await using var provider = SeederHost.Build(log, opens, onCommittedSettings: false, [.. settings]);
        var reached = false;

        var exitCode = await MigrateCommand.RunAsync(
            provider,
            TimeSpan.Zero,
            CancellationToken.None,
            _ =>
            {
                reached = true;
                return Task.FromResult(new GateResult(GateVerdict.TimedOut, null));
            });

        using var all = new AssertionScope();
        exitCode.Should().Be(2, "refused before any connection was opened");
        reached.Should().BeFalse("the wait is not started with a limit that bounds nothing");
        Errors(log).Should().ContainSingle()
            .Which.Should().Contain("migrate refused")
            .And.Contain("connect timeout is 0")
            .And.Contain("Nothing was opened");
        opens.Opens.Should().Be(0);
    }

    [Fact]
    public async Task Migrate_OnAnAzureSqlServerWithoutTheDatabase_Fails_AndNeverCreatesIt()
    {
        // EF answers "cannot open the database" with CREATE DATABASE. On Azure SQL that is a new
        // database at the service's default size, so the wait's verdict stops the command before
        // EF is asked anything. The verdict is handed in: no test server can be an Azure SQL one.
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        await using var provider = SeederHost.Build(
            log, opens, onCommittedSettings: false, (ConnectionKey, AzureConnection), NoEfRetry);

        var exitCode = await MigrateCommand.RunAsync(
            provider,
            TimeSpan.Zero,
            CancellationToken.None,
            _ => Task.FromResult(new GateResult(GateVerdict.NoSuchDatabase, "4060, class 11")));

        using var all = new AssertionScope();
        exitCode.Should().Be(1, "the server was contacted, so this is a failure and not a refusal");
        Errors(log).Should().ContainSingle()
            .Which.Should().Contain("does not exist on this Azure SQL server")
            .And.Contain("migrate does not create databases on Azure SQL");
        opens.Opens.Should().Be(0, "MigrateAsync, which would create the database, is never called");
        log.Lines.Should().Contain(
            line => line.Message.StartsWith("Database limits:", StringComparison.Ordinal),
            "the limits are logged before the wait, without opening anything");
    }

    [Theory]
    [InlineData("tcp:" + AzureName + ",1433", true)]
    [InlineData("127.0.0.1,1", false)]
    public async Task Migrate_WithTheRealWait_KeepsAFirstAnswerItDoesNotJudgeFromEf_OnAnAzureSqlNameOnly(
        string server, bool azureSql)
    {
        // The real wait on the string the command opens with: no verdict is handed in. The string
        // asks for two ways to sign in at once. SqlClient's parser, which the commands read it
        // with, accepts that; SqlClient refuses it when the connection is made, before it reaches
        // for the network. So the wait's first open fails with a failure that is not SQL Server's,
        // on any machine. Off Azure that goes to EF at once, which meets the same failure. On an
        // Azure SQL name the command has to tell the wait so, and the wait keeps the database from
        // EF, whose next step could be the CREATE DATABASE it answers a missing database with.
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        var twoWaysToSignIn =
            $"Server={server};Database=x;User Id=u;Password=not-a-secret;Connect Timeout=1;"
            + "Authentication=Active Directory Integrated";
        await using var provider = SeederHost.Build(
            log, opens, onCommittedSettings: false, (ConnectionKey, twoWaysToSignIn), NoEfRetry);

        var exitCode = await MigrateCommand.RunAsync(provider, TimeSpan.Zero, CancellationToken.None);

        var waited = log.Lines
            .Where(line => line.Level == LogLevel.Warning)
            .Select(line => line.Message)
            .Where(message => message.StartsWith("Waiting for the database (", StringComparison.Ordinal))
            .ToList();

        using var all = new AssertionScope();
        exitCode.Should().Be(1);
        opens.Opens.Should().Be(0, "the failure comes before any open is started");
        if (azureSql)
        {
            waited.Should().ContainSingle()
                .Which.Should().StartWith("Waiting for the database (ArgumentException, not an answer from SQL Server).");
            Errors(log).Should().ContainSingle()
                .Which.Should().Contain("migrate failed: the database did not accept a connection within 0 s")
                .And.Contain("the last answer was ArgumentException, not an answer from SQL Server");
        }
        else
        {
            // The control: the same string on a name that is not Azure's goes past the wait, and
            // the one Error is the failure EF met.
            waited.Should().BeEmpty();
            Errors(log).Should().ContainSingle()
                .Which.Should().StartWith("migrate failed: ")
                .And.NotContain("did not accept a connection");
        }
    }

    [Theory]
    [InlineData(GateVerdict.TimedOut, "did not accept a connection within")]
    [InlineData(GateVerdict.LoginRefused, "the login was refused three times")]
    [InlineData(GateVerdict.CannotOpenDatabase, "this login cannot open the database")]
    public async Task Migrate_OnAnyVerdictButProceed_Fails_WithoutAskingEf(GateVerdict verdict, string sentence)
    {
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        await using var provider = SeederHost.Build(
            log, opens, onCommittedSettings: false, (ConnectionKey, AbsentServer), NoEfRetry);

        var exitCode = await MigrateCommand.RunAsync(
            provider,
            TimeSpan.FromSeconds(7),
            CancellationToken.None,
            _ => Task.FromResult(new GateResult(verdict, "258, class 20: the wait operation timed out")));

        using var all = new AssertionScope();
        exitCode.Should().Be(1);
        Errors(log).Should().ContainSingle().Which.Should().Contain(sentence);
        opens.Opens.Should().Be(0);
    }

    [Fact]
    public async Task Migrate_OnARefusedLogin_SaysWhatToCheck_ForAPasswordAndForAManagedIdentity()
    {
        // The Azure deployment signs in as a managed identity (infra/README.md): its string holds
        // no password to check, and a refused login there means the database has no user for the
        // identity. The sentence has to be true for both ways to sign in. It names the variable,
        // never what the variable holds.
        var log = new RecordingLoggerProvider();
        await using var provider = SeederHost.Build(
            log, interceptor: null, onCommittedSettings: false, (ConnectionKey, AbsentServer), NoEfRetry);

        var exitCode = await MigrateCommand.RunAsync(
            provider,
            TimeSpan.FromSeconds(7),
            CancellationToken.None,
            _ => Task.FromResult(new GateResult(GateVerdict.LoginRefused, "18456, class 14: Login failed for user 'u'")));

        using var all = new AssertionScope();
        exitCode.Should().Be(1);
        Errors(log).Should().ContainSingle().Which.Should().Be(
            "migrate failed: the login was refused three times; check what "
            + "ConnectionStrings__DefaultConnection signs in with: a user and its password, or a managed "
            + "identity, which needs a user of its own in this database. "
            + "The last answer was 18456, class 14: Login failed for user 'u'.");
    }

    [Fact]
    public async Task Migrate_NeverReadsThePinPepper()
    {
        // The deployment's migrate job gets the connection string and no other secret. With no
        // pepper configured the command still reaches its wait.
        var log = new RecordingLoggerProvider();
        var reached = false;
        await using var provider = SeederHost.Build(
            log, interceptor: null, onCommittedSettings: false, (ConnectionKey, AbsentServer), NoEfRetry);

        var exitCode = await MigrateCommand.RunAsync(
            provider,
            TimeSpan.Zero,
            CancellationToken.None,
            _ =>
            {
                reached = true;
                return Task.FromResult(new GateResult(GateVerdict.TimedOut, null));
            });

        using var all = new AssertionScope();
        reached.Should().BeTrue();
        exitCode.Should().Be(1);
        log.Lines.Select(line => line.Message).Should().NotContain(message => message.Contains("PinPepper"));
    }

    [Theory]
    [InlineData("seed-pool", null)]
    [InlineData("seed-pool", "false")]
    [InlineData("recycle", null)]
    [InlineData("recycle", "false")]
    public async Task WithTheDemoOff_SeedPoolAndRecycle_AreRefused_AndNothingIsOpened(string command, string? flag)
    {
        // Off is the default, and it is what a job gets whose environment lost the variable. A
        // pool command run on a database that is not the demo's would write demo users there, or
        // delete. The builder and the recycler refuse too, by throwing; the command refuses first,
        // with a sentence, and with the code a job reads as "change the configuration".
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        await using var provider = SeederHost.Build(
            log,
            opens,
            onCommittedSettings: false,
            (ConnectionKey, AbsentServer),
            (PepperKey, SeederHost.Pepper),
            NoEfRetry,
            (DemoKey, flag));

        var exitCode = await Run(command, provider);

        using var all = new AssertionScope();
        exitCode.Should().Be(2, "refused before any connection was opened");
        Errors(log).Should().ContainSingle()
            .Which.Should().Contain($"{command} refused: Demo:Enabled is not true")
            .And.Contain("Nothing was opened");
        opens.Opens.Should().Be(0);
    }

    [Theory]
    [InlineData("seed", "seed's four users have a password and a PIN that are public")]
    [InlineData("reset", "reset drops the database")]
    public async Task InDemoMode_SeedAndReset_AreRefused_AndNothingIsOpened(string command, string reason)
    {
        // In demo mode the database is the pool's: seed would add users anybody can sign in as,
        // beside the private copies, and reset would drop the copies visitors hold.
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        await using var provider = SeederHost.Build(
            log,
            opens,
            onCommittedSettings: false,
            (ConnectionKey, AbsentServer),
            (PepperKey, SeederHost.Pepper),
            NoEfRetry,
            (DemoKey, "true"));

        var exitCode = await Run(command, provider);

        using var all = new AssertionScope();
        exitCode.Should().Be(2, "refused before any connection was opened");
        Errors(log).Should().ContainSingle()
            .Which.Should().Contain($"{command} refused: Demo:Enabled is true")
            .And.Contain(reason)
            .And.Contain("Nothing was opened");
        opens.Opens.Should().Be(0);
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("reset")]
    [InlineData("seed-pool")]
    [InlineData("recycle")]
    public async Task ADemoSettingOutOfRange_IsRefused_ByEveryCommandThatReadsTheDemosSettings(string command)
    {
        // The validator runs with the pepper's, by hand: this tool never starts the host, so
        // ValidateOnStart alone would not fire. Each of these four reads the demo's settings.
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        await using var provider = SeederHost.Build(
            log,
            opens,
            onCommittedSettings: false,
            (ConnectionKey, AbsentServer),
            (PepperKey, SeederHost.Pepper),
            NoEfRetry,
            TheFlagItRunsWith(command),
            ("Demo:Pool:TargetFree", "0"));

        var exitCode = await Run(command, provider);

        using var all = new AssertionScope();
        exitCode.Should().Be(2);
        Errors(log).Should().ContainSingle()
            .Which.Should().Contain($"{command} refused: Demo:Pool:TargetFree must be between 1 and 500, and is 0.")
            .And.Contain("Nothing was opened");
        opens.Opens.Should().Be(0);
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("reset")]
    [InlineData("seed-pool")]
    [InlineData("recycle")]
    public async Task APepperAndADemoSettingThatBothFail_AreRefusedTogether_InOneLine(string command)
    {
        // Each validator alone throws its own failure. Both together, the start-up validator throws
        // the two inside one AggregateException: that used to reach the command's catch-all and end
        // as exit 1 with a stack, the code that says "run it again".
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        await using var provider = SeederHost.Build(
            log,
            opens,
            onCommittedSettings: false,
            (ConnectionKey, AbsentServer),
            NoEfRetry,
            TheFlagItRunsWith(command),
            ("Demo:Pool:TargetFree", "0"));

        var exitCode = await Run(command, provider);

        using var all = new AssertionScope();
        exitCode.Should().Be(2, "a configuration that cannot run is a refusal, whatever else is wrong with it");
        Errors(log).Should().ContainSingle()
            .Which.Should().Contain($"{command} refused: ")
            .And.Contain("Security:PinPepper must be configured with at least 32 characters")
            .And.Contain("Demo:Pool:TargetFree must be between 1 and 500, and is 0.")
            .And.Contain("Nothing was opened")
            .And.NotContain("   at ", "the refusal is a sentence, not a stack trace");
        opens.Opens.Should().Be(0);
    }

    [Theory]
    [InlineData("seed", "Demo:Enabled", "Boolean")]
    [InlineData("reset", "Demo:Enabled", "Boolean")]
    [InlineData("seed-pool", "Demo:Pool:TargetFree", "Int32")]
    [InlineData("recycle", "Demo:Enabled", "Boolean")]
    [InlineData("recycle", "Security:PinPepperKeyId", "Int32")]
    public async Task ASettingThatCannotBeRead_IsRefused_NamingItsKeyAndNotItsValue(string command, string key, string type)
    {
        // "yes" for a flag, a word for a number: the binder throws before any validator runs, with
        // a message that quotes the value. The value is not printed: the key is what to fix.
        const string Unreadable = "canary-value-7f3a";
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        var settings = new List<(string Key, string? Value)> { (ConnectionKey, AbsentServer), (PepperKey, SeederHost.Pepper), NoEfRetry };
        if (key != DemoKey)
        {
            settings.Add(TheFlagItRunsWith(command));
        }

        settings.Add((key, Unreadable));
        await using var provider = SeederHost.Build(log, opens, onCommittedSettings: false, [.. settings]);

        var exitCode = await Run(command, provider);

        var output = string.Join('\n', log.Lines.Select(line => line.Message));
        using var all = new AssertionScope();
        exitCode.Should().Be(2, "an unreadable setting is a refusal, exit 2, as one out of range is");
        Errors(log).Should().ContainSingle()
            .Which.Should().Contain($"{command} refused: {key} holds a value that cannot be read as {type}.")
            .And.Contain("Nothing was opened")
            .And.NotContain("   at ", "the refusal is a sentence, not a stack trace");
        output.Should().NotContain("canary", "the value is not printed, only the key that holds it");
        opens.Opens.Should().Be(0);
    }

    [Theory]
    [InlineData("seed-pool")]
    [InlineData("recycle")]
    public async Task OnAnAzureSqlName_SeedPoolAndRecycle_AreNotRefused_AndReachForTheDatabase(string command)
    {
        // The demo's database is an Azure SQL one, and these two are the commands that must run
        // there; seed and reset are refused on the same name (above). The instrument stops the
        // open, so nothing leaves the machine.
        var log = new RecordingLoggerProvider();
        var opens = new RefuseEveryOpen();
        await using var provider = SeederHost.Build(
            log,
            opens,
            onCommittedSettings: false,
            (ConnectionKey, AzureConnection),
            (PepperKey, SeederHost.Pepper),
            NoEfRetry,
            (DemoKey, "true"));

        var exitCode = await Run(command, provider);

        using var all = new AssertionScope();
        exitCode.Should().Be(1, "the database was reached for, so this is a failure and not a refusal");
        opens.Opens.Should().BeGreaterThan(0);
        Errors(log).Should().NotContain(message => message.Contains("refused"));
        Errors(log).Should().Contain(
            message => message.StartsWith($"{command} failed: ", StringComparison.Ordinal)
                && message.Contains(RefuseEveryOpen.Message));
    }

    [Theory]
    [InlineData("seed-pool")]
    [InlineData("recycle")]
    public async Task APoolRunThatIsCancelled_Fails_SaysSo_AndPrintsNoSummary(string command)
    {
        // A run that did not finish has no counts to report: its line would be a guess.
        var log = new RecordingLoggerProvider();
        await using var provider = SeederHost.Build(
            log,
            interceptor: null,
            onCommittedSettings: false,
            (ConnectionKey, AbsentServer),
            (PepperKey, SeederHost.Pepper),
            NoEfRetry,
            (DemoKey, "true"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var exitCode = await Run(command, provider, cancelled.Token);

        var messages = log.Lines.Select(line => line.Message).ToList();
        using var all = new AssertionScope();
        exitCode.Should().Be(1);
        messages.Should().Contain(message => message.Contains($"{command} was cancelled"));
        messages.Should().NotContain(message => message.StartsWith("pool: ", StringComparison.Ordinal));
        Errors(log).Should().BeEmpty("being stopped is not a failure of the run's own");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("501")]
    public void SeedPool_RefusesACountOutsideOneTo500_AsAWrongCommandLine(string copies)
    {
        // The parser answers, with System.CommandLine's exit 1, before the handler is reached.
        // 500 is the most Demo:Pool:TargetFree accepts.
        var root = new RootCommand { SeedPoolCommand.Create(new ServiceCollection().BuildServiceProvider()) };

        var parsed = root.Parse(["seed-pool", copies]);

        parsed.Errors.Select(error => error.Message).Should().ContainSingle()
            .Which.Should().Be($"seed-pool takes a number of copies from 1 to 500, and was given {copies}.");
    }

    [Theory]
    [InlineData]
    [InlineData("1")]
    [InlineData("500")]
    public void SeedPool_TakesNoCount_OrOneFromOneTo500(params string[] copies)
    {
        var root = new RootCommand { SeedPoolCommand.Create(new ServiceCollection().BuildServiceProvider()) };

        var parsed = root.Parse(["seed-pool", .. copies]);

        parsed.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ACancelledSeed_Fails_AndDoesNotClaimSuccess()
    {
        // The orchestrator used to leave its loop on a cancelled token and then log "Database
        // seeding completed successfully", and the command printed "Database seeded successfully!".
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        await using var provider = SeederHost.Build(
            log, opens, onCommittedSettings: false, (ConnectionKey, AbsentServer), (PepperKey, SeederHost.Pepper), NoEfRetry);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var exitCode = await SeedCommand.RunAsync(provider, cancelled.Token);

        var messages = log.Lines.Select(line => line.Message).ToList();
        using var all = new AssertionScope();
        exitCode.Should().Be(1);
        messages.Should().Contain(message => message.Contains("seed was cancelled"));
        messages.Should().NotContain(message => message.Contains("successfully"));
        opens.Opens.Should().Be(0, "the token was cancelled before the first seeder ran");
    }

    [Fact]
    public async Task ASeedCancelledWhileIdentityOpensAConnection_IsCancelledAtThatOpen()
    {
        // The first thing seed asks the database is RoleManager.RoleExistsAsync, and Identity's
        // managers take no token: each reads one from a property that answers "none" unless it is
        // overridden. So a stop that arrived while SQL Server was away cancelled nothing, the open
        // went on through EF's whole retry budget, and the container was killed without a line
        // (exit 137, measured 2026-10-01 in the tools image, three runs of three).
        var log = new RecordingLoggerProvider();
        using var run = new CancellationTokenSource();
        var probe = new CancelAtTheOpen(run);
        await using var provider = SeederHost.Build(
            log, probe, onCommittedSettings: false, (ConnectionKey, AbsentServer), (PepperKey, SeederHost.Pepper), NoEfRetry);

        var exitCode = await SeedCommand.RunAsync(provider, run.Token);

        using var all = new AssertionScope();
        probe.Seen.Should().Equal([true], "the open in flight carried the run's token, and nothing was opened after it");
        exitCode.Should().Be(1);
        log.Lines.Select(line => line.Message).Should().Contain(message => message.Contains("seed was cancelled"));
    }

    [Theory]
    [InlineData("roles")]
    [InlineData("users")]
    public async Task EachIdentityManagerTheToolRegisters_OpensWithTheRunsToken(string manager)
    {
        // The seed above stops at its first open, which is the role manager's. This asks each
        // manager on its own, so the user manager's override is not covered by the other's.
        using var run = new CancellationTokenSource();
        var probe = new CancelAtTheOpen(run);
        await using var provider = SeederHost.Build(
            new RecordingLoggerProvider(),
            probe,
            onCommittedSettings: false,
            (ConnectionKey, AbsentServer),
            (PepperKey, SeederHost.Pepper),
            NoEfRetry);
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RunCancellation>().Token = run.Token;

        Func<Task> call = manager == "roles"
            ? () => scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>().RoleExistsAsync("User")
            : () => scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(Guid.Empty.ToString());

        await call.Should().ThrowAsync<OperationCanceledException>();
        probe.Seen.Should().Equal([true]);
    }

    [Fact]
    public async Task Seed_OnAServerThatIsNotThere_Fails_AndTheOpenIsCounted()
    {
        // The control for every "0 opens" above: the same instrument on the same registration, and
        // a string that is neither Azure nor refused. The command reaches for the database, the
        // count moves, and the failure is one Error and exit 1 rather than an unhandled exception.
        var log = new RecordingLoggerProvider();
        var opens = new OpenCountingInterceptor();
        await using var provider = SeederHost.Build(
            log, opens, onCommittedSettings: false, (ConnectionKey, AbsentServer), (PepperKey, SeederHost.Pepper), NoEfRetry);

        var exitCode = await SeedCommand.RunAsync(provider, CancellationToken.None);

        using var all = new AssertionScope();
        exitCode.Should().Be(1, "the server was tried and did not answer");
        opens.Opens.Should().BeGreaterThan(0);
        Errors(log).Should().Contain(message => message.StartsWith("seed failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// Cancels the run at the moment EF starts to open a connection, and records whether the token
    /// that open was given saw it. An open that did is stopped there; one that did not goes on to
    /// the server that is not there, as it would in a container that was told to stop.
    /// </summary>
    private sealed class CancelAtTheOpen(CancellationTokenSource run) : DbConnectionInterceptor
    {
        private readonly List<bool> _seen = [];

        /// <summary>For each open EF started: whether its own token was cancelled with the run.</summary>
        public IReadOnlyList<bool> Seen
        {
            get
            {
                lock (_seen)
                {
                    return _seen.ToArray();
                }
            }
        }

        public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            await run.CancelAsync();
            lock (_seen)
            {
                _seen.Add(cancellationToken.IsCancellationRequested);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    /// <summary>
    /// Counts each open EF starts and stops it there, with a failure EF does not retry: a command
    /// that reaches for the database is seen to, and nothing leaves the machine.
    /// </summary>
    private sealed class RefuseEveryOpen : DbConnectionInterceptor
    {
        public const string Message = "Stopped by the test: no connection is opened.";

        private int _opens;

        /// <summary>How many times EF started to open a connection.</summary>
        public int Opens => Volatile.Read(ref _opens);

        public override InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            Interlocked.Increment(ref _opens);
            throw new InvalidOperationException(Message);
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _opens);
            throw new InvalidOperationException(Message);
        }
    }
}
