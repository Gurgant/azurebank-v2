using System.Diagnostics;
using AzureBank.Api.Extensions;
using AzureBank.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace AzureBank.Tests.Unit.HealthChecks;

/// <summary>
/// Readiness ANSWERS. Not "the registration has a timeout field set" — that it comes back.
/// </summary>
/// <remarks>
/// <para>
/// The sibling test asserts the registrations carry a finite timeout, which is the mechanism. This
/// asserts the consequence, end to end through the real <see cref="HealthCheckService"/>, because
/// the two are not the same claim: the framework has to honour the value for the field to mean
/// anything, and a source cited when this was raised claimed it does not.
/// </para>
/// <para>
/// MEASURED BEFORE THE FIX: pointed at an unroutable address the audit probe took <b>36,800 ms</b> —
/// SqlClient's connect timeout, a retry after <c>ConnectRetryInterval</c>, then the timeout again.
/// Kubernetes' default probe timeout is one second, so nothing was still listening. A probe that
/// never answers reports nothing while still holding a connection, which is worse than one that
/// answers "unhealthy".
/// </para>
/// </remarks>
public class ReadinessAnswersWithinBudgetTests
{
    private readonly ITestOutputHelper _output;

    public ReadinessAnswersWithinBudgetTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task WhenTheStoreCannotBeReachedAtAll_ReadinessStillComesBackInsideTheBudget()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Development");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Audit:TailTimeoutSeconds"] = "1" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // RFC 5737 TEST-NET-1: guaranteed unroutable, so the connection attempt HANGS rather than
        // being refused. A refused connection fails fast and would prove nothing about a bound.
        services.AddDbContext<AzureBankDbContext>(options => options.UseSqlServer(
            "Server=192.0.2.1;Database=X;User Id=u;Password=p;TrustServerCertificate=True"));
        services.AddObservability(environment.Object, configuration);

        var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<HealthCheckService>();

        var stopwatch = Stopwatch.StartNew();
        var report = await health.CheckHealthAsync(r => r.Tags.Contains("ready"));
        stopwatch.Stop();

        _output.WriteLine(
            $"unreachable store, budget 1s -> answered {report.Status} in {stopwatch.ElapsedMilliseconds}ms "
            + $"(unbounded, this measured 36,800ms)");

        stopwatch.ElapsedMilliseconds.Should().BeLessThan(
            5_000,
            "the whole point is that readiness ANSWERS. Unbounded this took 36.8 seconds, by which "
            + "time every caller had given up and the endpoint was reporting nothing to anyone");

        report.Status.Should().Be(
            HealthStatus.Unhealthy,
            "answering 'unhealthy' is the useful outcome — an instance that cannot read the audit "
            + "store cannot move money, and saying so is what takes it out of rotation");
    }
}
