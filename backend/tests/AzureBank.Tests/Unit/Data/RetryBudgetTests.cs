using AzureBank.Infrastructure.Data;
using AzureBank.Infrastructure.Extensions;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// The retry budget EF runs with comes from <c>Database:MaxRetryCount</c> and
/// <c>Database:MaxRetryDelay</c>, through the registration every writer uses
/// (<c>AddInfrastructure</c>), and without them it is 4 retries, back-off capped at 10 s
/// (ADR-0058): an outage that outlasts about 12 s of back-off (32 s on the throttling codes)
/// answers 503 rather than holding the request.
/// </summary>
/// <remarks>
/// The count is observed by running the context's own execution strategy over a delegate that
/// throws a bare <see cref="TimeoutException"/>, which EF's SQL Server detector treats as transient
/// (ADR-0034); no connection is ever opened. The delay cap is read off the same strategy object.
/// </remarks>
public class RetryBudgetTests
{
    private static (ExecutionStrategy Strategy, IServiceScope Scope, ServiceProvider Provider) Strategy(
        ILoggerProvider? logger, params (string Key, string Value)[] database)
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
        services.AddLogging(builder =>
        {
            if (logger is not null)
            {
                builder.AddProvider(logger);
            }
        });
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), environment.Object);
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var strategy = context.Database.CreateExecutionStrategy().Should().BeAssignableTo<ExecutionStrategy>().Subject;
        return (strategy, scope, provider);
    }

    private static int RunToExhaustion(ExecutionStrategy strategy)
    {
        var attempts = 0;
        var act = () => strategy.Execute(() =>
        {
            attempts++;
            throw new TimeoutException("transient, as far as the detector is concerned");
        });

        act.Should().Throw<RetryLimitExceededException>();
        return attempts;
    }

    [Fact]
    public void TheConfiguredBudget_IsTheOneEfRetriesWith()
    {
        var (strategy, scope, provider) = Strategy(
            null, ("Database:MaxRetryCount", "2"), ("Database:MaxRetryDelay", "00:00:00.001"));
        using (provider)
        using (scope)
        {
            var maxRetryDelay = strategy.MaxRetryDelay;
            var attempts = RunToExhaustion(strategy);

            attempts.Should().Be(3, "two retries after the first attempt");
            strategy.MaxRetryCount.Should().Be(2, "the count read in the test below is the one EF runs by");
            maxRetryDelay.Should().Be(TimeSpan.FromMilliseconds(1));
        }
    }

    [Fact]
    public void NothingConfigured_IsFourRetriesAtMostTenSecondsApart()
    {
        // READ, NOT RUN. Running the default budget would wait out EF's own back-off, 0 + 1 + 3 + 7 s
        // with jitter, about 12 s of a unit run. The test above runs a configured budget and shows
        // that MaxRetryCount is the count EF retries by, so reading it here is the same observation.
        // Until this PR the default was 3 retries under a 30-second cap, which waited 4 to 5 s here.
        var (strategy, scope, provider) = Strategy(null);
        using (provider)
        using (scope)
        {
            strategy.MaxRetryCount.Should().Be(4, "four retries after the first attempt (ADR-0058)");
            strategy.MaxRetryDelay.Should().Be(TimeSpan.FromSeconds(10), "EF's back-off is capped at 10 s (ADR-0058)");
        }
    }

    [Fact]
    public void ARetry_IsLoggedAtWarning()
    {
        // The host's Serilog override holds Microsoft.EntityFrameworkCore at Warning, and EF raises
        // ExecutionStrategyRetrying at Information: every retry of an outage was invisible in the
        // log. AddInfrastructure raises the event to Warning (ADR-0058). The event is made DUE here
        // (one retry, one-millisecond cap) before its level is read, so a silence cannot pass.
        var recorder = new RecordingLoggerProvider();
        var (strategy, scope, provider) = Strategy(
            recorder, ("Database:MaxRetryCount", "1"), ("Database:MaxRetryDelay", "00:00:00.001"));
        using (provider)
        using (scope)
        {
            RunToExhaustion(strategy).Should().Be(2, "one retry after the first attempt");
        }

        var retrying = recorder.Lines
            .Where(line => line.Message.Contains("will be retried after", StringComparison.Ordinal))
            .ToList();

        retrying.Should().ContainSingle("EF raises ExecutionStrategyRetrying once per retry, and one retry ran");
        retrying[0].Level.Should().Be(
            LogLevel.Warning,
            "a retry during an outage is the first sign of it, and the host drops EF's Information lines");
    }
}
