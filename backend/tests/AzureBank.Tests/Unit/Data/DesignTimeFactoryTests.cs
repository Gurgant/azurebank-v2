using AzureBank.Infrastructure.Data;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// The context <c>dotnet ef</c> gets from <see cref="DesignTimeDbContextFactory"/>: it is built
/// from any folder, it retries with the budget every host has (ADR-0058), and the connection string
/// it reads from the API's settings opens with the hosts' connection limits.
/// </summary>
/// <remarks>
/// <para>
/// Before, the factory required <c>../AzureBank.Api/appsettings.json</c> relative to the current
/// directory and threw without it, so it could only run inside the repository; and it handed the
/// raw string to <c>UseSqlServer</c> with no retrying strategy, which is what ADR-0058 named as a
/// precondition for running migrations against a deployment.
/// </para>
/// <para>
/// <c>dotnet ef … --connection</c> sets the connection string AFTER the factory has run
/// (<c>MigrationsOperations.UpdateDatabase</c> calls <c>SetConnectionString</c> on the context the
/// factory returned). So such a run keeps the retry budget and opens with its own string's limits;
/// the last test pins both halves.
/// </para>
/// <para>
/// The factory is called with the folder as an argument, never by changing the process's current
/// directory, which every other test in the run shares. No connection is opened.
/// </para>
/// </remarks>
public sealed class DesignTimeFactoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"azurebank-design-time-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>A folder standing in for the Infrastructure project's, with no API project beside it.</summary>
    private string FolderWithoutTheApi()
    {
        var folder = Path.Combine(_root, "AzureBank.Infrastructure");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>The same, with an API folder beside it whose settings hold <paramref name="connectionString"/>.</summary>
    private string FolderBesideAnApiWith(string connectionString)
    {
        var folder = FolderWithoutTheApi();
        var api = Path.Combine(_root, "AzureBank.Api");
        Directory.CreateDirectory(api);
        File.WriteAllText(
            Path.Combine(api, "appsettings.json"),
            $$"""{ "ConnectionStrings": { "DefaultConnection": "{{connectionString.Replace(@"\", @"\\")}}" } }""");
        return folder;
    }

    [Fact]
    public void FromAFolderWithNoApiProject_AContextIsBuilt_AndTheModelMatchesTheMigrations()
    {
        using var context = DesignTimeDbContextFactory.Create(FolderWithoutTheApi());

        using var all = new AssertionScope();
        context.Database.GetConnectionString().Should().BeNull("no settings file was there to read one from");

        // The check CI runs through `dotnet ef migrations has-pending-model-changes`, on the same
        // context: a changed entity with no migration fails here too, without the tool.
        context.Database.HasPendingModelChanges().Should().BeFalse("every model change has a migration");
    }

    [Fact]
    public void ItRetriesWithTheBudgetEveryHostHas()
    {
        using var context = DesignTimeDbContextFactory.Create(FolderWithoutTheApi());

        var strategy = context.Database.CreateExecutionStrategy().Should().BeAssignableTo<ExecutionStrategy>(
            "a migration run against a deployment has to survive a transient fault").Subject;
        using var all = new AssertionScope();
        strategy.MaxRetryCount.Should().Be(4);
        strategy.MaxRetryDelay.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void TheStringItReads_OpensWithTheConnectionLimitsOfTheHosts()
    {
        using var context = DesignTimeDbContextFactory.Create(
            FolderBesideAnApiWith(@"Server=(localdb)\MSSQLLocalDB;Database=X;Trusted_Connection=True"));

        var opened = new SqlConnectionStringBuilder(context.Database.GetConnectionString());
        using var all = new AssertionScope();
        opened.DataSource.Should().Be(@"(localdb)\MSSQLLocalDB");
        opened.ConnectTimeout.Should().Be(10);
        opened.ConnectRetryCount.Should().Be(0);
        opened.MaxPoolSize.Should().Be(12);
        opened.PoolBlockingPeriod.Should().Be(PoolBlockingPeriod.NeverBlock);
    }

    [Fact]
    public void ALimitTheStringSets_Wins()
    {
        using var context = DesignTimeDbContextFactory.Create(
            FolderBesideAnApiWith(@"Server=(localdb)\MSSQLLocalDB;Database=X;Trusted_Connection=True;Max Pool Size=50;Connect Timeout=25"));

        var opened = new SqlConnectionStringBuilder(context.Database.GetConnectionString());
        using var all = new AssertionScope();
        opened.MaxPoolSize.Should().Be(50);
        opened.ConnectTimeout.Should().Be(25);
        opened.ConnectRetryCount.Should().Be(0, "a limit the string leaves unset is still filled in");
    }

    [Fact]
    public void AStringSetAfterwards_AsTheConnectionOptionDoes_KeepsTheRetryBudget_AndIsOpenedAsWritten()
    {
        const string FromTheCommandLine = "Server=127.0.0.1,1;Database=X;User Id=u;Password=not-a-secret";
        using var context = DesignTimeDbContextFactory.Create(
            FolderBesideAnApiWith(@"Server=(localdb)\MSSQLLocalDB;Database=X;Trusted_Connection=True"));

        context.Database.SetConnectionString(FromTheCommandLine);

        var strategy = context.Database.CreateExecutionStrategy().Should().BeAssignableTo<ExecutionStrategy>().Subject;
        using var all = new AssertionScope();
        strategy.MaxRetryCount.Should().Be(4, "the budget belongs to the context, not to the string");
        context.Database.GetConnectionString().Should().Be(
            FromTheCommandLine,
            "the limits are written into the string the factory reads, and this one replaced it: "
            + "a --connection run opens with SqlClient's defaults unless the string sets its own");
    }
}
