extern alias seeder;

using System.Globalization;
using System.Reflection;
using AzureBank.Api.Extensions;
using AzureBank.Infrastructure.Data;
using AzureBank.Infrastructure.Extensions;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Serilog.Events;
using Xunit;
using SeederServices = seeder::AzureBank.Seeder.Extensions.ServiceCollectionExtensions;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// The connection limits every host runs with come from code (ADR-0058), applied by
/// <c>AddInfrastructure</c> to whatever connection string it is given, and only where that string
/// leaves a keyword unset: 10 s to connect, no SqlClient connect retry (EF's retry is the only
/// layer), a pool of 12, and <c>Pool Blocking Period=NeverBlock</c>.
/// </summary>
/// <remarks>
/// <para>
/// Before, the raw string went to <c>UseSqlServer</c> and SqlClient's own defaults applied: 15 s,
/// one connect retry, a pool of 100, and blocking on. Off Azure, blocking replays a failed open's
/// error for 5 s, doubling up to 60 s, so a local or CI run kept failing after SQL Server was back,
/// which Azure never does (<c>Auto</c> turns blocking off on <c>*.database.windows.net</c>).
/// </para>
/// <para>
/// Read from the context the registration builds, <c>Database.GetConnectionString()</c>: the
/// string a connection is actually opened with, not the configuration it came from. No connection
/// is opened.
/// </para>
/// </remarks>
public class ConnectionDefaultsTests
{
    private const string BareConnectionString = @"Server=(localdb)\MSSQLLocalDB;Database=X;Trusted_Connection=True";

    private static SqlConnectionStringBuilder Effective(
        string connectionString, params (string Key, string Value)[] database)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString,
        };
        foreach (var (key, value) in database)
        {
            values[key] = value;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), Production());
        return Opened(services);
    }

    private static IHostEnvironment Production()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Production");
        return environment.Object;
    }

    private static SqlConnectionStringBuilder Opened(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return new SqlConnectionStringBuilder(context.Database.GetConnectionString());
    }

    [Fact]
    public void AStringThatSetsNoLimit_OpensWithTheCodeDefaults()
    {
        var effective = Effective(BareConnectionString);

        using var all = new AssertionScope();
        effective.ConnectTimeout.Should().Be(10, "each login, and each BEGIN, COMMIT and ROLLBACK, is bounded by it");
        effective.ConnectRetryCount.Should().Be(0, "EF's execution strategy is the only retry layer");
        effective.MaxPoolSize.Should().Be(12, "2 x 12 + 5 for the jobs stays within the database's 30 logins");
        effective.PoolBlockingPeriod.Should().Be(
            PoolBlockingPeriod.NeverBlock,
            "what Auto already does on Azure, so a local or CI outage ends when SQL Server is back");
    }

    [Fact]
    public void TheConfiguredLimits_AreTheOnesApplied()
    {
        var effective = Effective(
            BareConnectionString,
            ("Database:ConnectTimeoutSeconds", "7"),
            ("Database:ConnectRetryCount", "2"),
            ("Database:MaxPoolSize", "5"));

        using var all = new AssertionScope();
        effective.ConnectTimeout.Should().Be(7);
        effective.ConnectRetryCount.Should().Be(2);
        effective.MaxPoolSize.Should().Be(5);
        effective.PoolBlockingPeriod.Should().Be(PoolBlockingPeriod.NeverBlock);
    }

    [Fact]
    public void AValueTheStringSets_Wins_UnderAnyOfItsNames()
    {
        // A guard, green before and after: the defaults fill gaps and never override. "Connection
        // Timeout" is a synonym of "Connect Timeout", so it counts as set.
        var effective = Effective(
            BareConnectionString
            + ";Connection Timeout=25;ConnectRetryCount=3;Max Pool Size=50;Pool Blocking Period=AlwaysBlock");

        effective.ConnectTimeout.Should().Be(25);
        effective.ConnectRetryCount.Should().Be(3);
        effective.MaxPoolSize.Should().Be(50);
        effective.PoolBlockingPeriod.Should().Be(PoolBlockingPeriod.AlwaysBlock);
    }

    [Fact]
    public void AMinPoolSizeAboveTheDefaultPool_RaisesThePoolToIt()
    {
        // A guard for the same rule from the other side: a string that sets only Min Pool Size=20
        // was valid under SqlClient's pool of 100. Filling in a pool of 12 beneath it would make
        // SqlClient refuse every connection (min above max), so a default must not turn a string
        // that worked into one that cannot open.
        var effective = Effective(BareConnectionString + ";Min Pool Size=20");

        using var all = new AssertionScope();
        effective.MinPoolSize.Should().Be(20);
        effective.MaxPoolSize.Should().Be(20, "the pool has to hold the connections the string keeps warm");
        var create = () => new SqlConnection(effective.ConnectionString).Dispose();
        create.Should().NotThrow("SqlClient checks min against max when a connection is created");
    }

    [Fact]
    public void TheSeeder_OpensWithAPoolOfFive()
    {
        // The seeder's jobs share the database's 30 logins with the API: 2 x 12 + 5 = 29. Its pool
        // is image configuration (its appsettings.json), read here through the seeder's own
        // registration, so no Database__ variable is needed where it runs.
        var path = Path.Combine(RepoRoot(), "backend", "tools", "AzureBank.Seeder", "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        using var all = new AssertionScope();
        configuration["Database:MaxPoolSize"].Should().Be("5", $"the seeder's pool is set in {path}");

        var services = new ServiceCollection();
        services.AddLogging();
        SeederServices.AddSeederServices(services, configuration, Production());

        Opened(services).MaxPoolSize.Should().Be(5);
    }

    [Fact]
    public void TheStartupLine_ReportsTheLimitsTheContextWasBuiltWith()
    {
        // The API logs the limits once it has started (Program.cs). The line is read back from the
        // context the registration builds, the same one a request gets, so it shows the code
        // defaults here and would show SqlClient's own (15, 1, 100, Auto) if they were not applied.
        var recorder = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(recorder));
        services.AddInfrastructure(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = BareConnectionString,
            }).Build(),
            Production());
        using var provider = services.BuildServiceProvider();

        WebApplicationExtensions.LogDatabaseLimits(provider);

        recorder.Lines.Where(line => line.Message.StartsWith("Database limits:", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Should().Be((
            LogLevel.Information,
            "Database limits: connect timeout 10 s, connect retries 0, pool 12, pool blocking NeverBlock; "
            + "EF retries 4, back-off capped at 00:00:10; request deadline 40 s"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(because: "the test reads the seeder's committed settings");
        return dir!.FullName;
    }
}

/// <summary>
/// The startup line on the API's own host: <c>Program.cs</c> has to register it, which
/// <see cref="ConnectionDefaultsTests"/> cannot show, since it calls the logging method directly.
/// On SQL Server, because a host on the in-memory provider has no connection string and logs none.
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class ConnectionDefaultsSqlServerTests
{
    [SqlServerFact]
    public void TheApi_LogsTheLimitsItOpensWith_OnceItHasStarted()
    {
        // The expected values come from the same string the factory opens with, so a local string
        // that sets its own limit (an explicit value wins) still passes. The factory's default SQL
        // host has no retrying strategy: EF retries 0.
        var connectionString = SqlServerFactAttribute.ConnectionString!;
        var expected = new SqlConnectionStringBuilder(
            SqlConnectionDefaults.Apply(connectionString, new DatabaseOptions()));
        using var factory = new CustomWebApplicationFactory();
        factory.SetConnectionString(connectionString);
        factory.CaptureLog(LogEventLevel.Information);

        using var client = factory.CreateClient();

        factory.CapturedEvents
            .Where(e => e.MessageTemplate.Text.StartsWith("Database limits:", StringComparison.Ordinal))
            .Select(e => e.RenderMessage(CultureInfo.InvariantCulture))
            .Should().ContainSingle("the API logs its limits once, when the host has started")
            .Which.Should().Be(
                $"Database limits: connect timeout {expected.ConnectTimeout} s, "
                + $"connect retries {expected.ConnectRetryCount}, pool {expected.MaxPoolSize}, "
                + $"pool blocking {expected.PoolBlockingPeriod}; EF retries 0, back-off capped at 00:00:00; "
                + "request deadline 40 s");
    }
}
