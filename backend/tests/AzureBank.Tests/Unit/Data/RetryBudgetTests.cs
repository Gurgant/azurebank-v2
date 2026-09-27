using AzureBank.Infrastructure.Data;
using AzureBank.Infrastructure.Extensions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// The retry budget EF runs with comes from <c>Database:MaxRetryCount</c> and
/// <c>Database:MaxRetryDelay</c>, through the registration every writer uses
/// (<c>AddInfrastructure</c>), and without them it is the 3 retries and 30 s the code always had.
/// </summary>
/// <remarks>
/// The count is observed by running the context's own execution strategy over a delegate that
/// throws a bare <see cref="TimeoutException"/>, which EF's SQL Server detector treats as transient
/// (ADR-0034); no connection is ever opened. The delay cap is read off the same strategy object.
/// </remarks>
public class RetryBudgetTests
{
    private static (int Attempts, TimeSpan MaxRetryDelay) Run(params (string Key, string Value)[] database)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = @"Server=(localdb)\MSSQLLocalDB;Database=X;Trusted_Connection=True",
        };
        foreach (var (key, value) in database)
        {
            values[key] = value;
        }

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Production");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), environment.Object);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var strategy = context.Database.CreateExecutionStrategy().Should().BeAssignableTo<ExecutionStrategy>().Subject;
        var maxRetryDelay = strategy.MaxRetryDelay;

        var attempts = 0;
        var act = () => strategy.Execute(() =>
        {
            attempts++;
            throw new TimeoutException("transient, as far as the detector is concerned");
        });

        act.Should().Throw<RetryLimitExceededException>();
        return (attempts, maxRetryDelay);
    }

    [Fact]
    public void TheConfiguredBudget_IsTheOneEfRetriesWith()
    {
        var (attempts, maxRetryDelay) = Run(("Database:MaxRetryCount", "2"), ("Database:MaxRetryDelay", "00:00:00.001"));

        attempts.Should().Be(3, "two retries after the first attempt");
        maxRetryDelay.Should().Be(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void NothingConfigured_IsTheBudgetTheCodeAlwaysHad()
    {
        // Slow on purpose: the three waits are EF's own backoff under the 30-second cap, and this
        // case took 4 to 5 s in the runner over four runs (2026-09-25), building the context
        // included -- today's whole budget, spent waiting.
        var (attempts, maxRetryDelay) = Run();

        attempts.Should().Be(4, "three retries after the first attempt");
        maxRetryDelay.Should().Be(TimeSpan.FromSeconds(30));
    }
}
