using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The public demo's API on SQL Server, on the database the pool's commands fill: each test has a
/// scratch database of its own (<see cref="DemoPoolDatabase"/>), copies built by the Seeder's own
/// builder, and an API with the demo on.
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class DemoClaimSqlServerTests
{
    private static DemoOptions DemoOf(CustomWebApplicationFactory api) =>
        api.Services.GetRequiredService<IOptions<DemoOptions>>().Value;

    private static string DatabaseOf(CustomWebApplicationFactory api)
    {
        using var scope = api.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AzureBankDbContext>().Database.GetDbConnection().Database;
    }

    [SqlServerFact]
    public async Task TheDemoApi_StartsOnThePoolsDatabase_WithTheFlagOn_AndTheOrdinaryApiKeepsItOff()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var scratch = new SqlConnectionStringBuilder(database.ConnectionString).InitialCatalog;

        var demoApi = database.DemoApi(("Demo:Claim:MaxPerClientPerDay", "3"));
        var ordinaryApi = database.Api();

        var demo = DemoOf(demoApi);
        demo.Enabled.Should().BeTrue();
        demo.ClientKeySecret.Should().Be(CustomWebApplicationFactory.DemoClientKeySecret);
        demo.Claim.MaxPerClientPerDay.Should().Be(3, "the demo API carries the settings a test gives it");
        DatabaseOf(demoApi).Should().Be(scratch, "both hosts are on the database this test created");

        // The host the pool's own tests register users and sign in through. It stays as it was.
        DemoOf(ordinaryApi).Enabled.Should().BeFalse();
        DatabaseOf(ordinaryApi).Should().Be(scratch);
    }
}
