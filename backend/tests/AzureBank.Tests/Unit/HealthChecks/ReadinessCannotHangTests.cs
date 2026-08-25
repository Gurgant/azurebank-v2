using System.Diagnostics;
using AzureBank.Api.Extensions;
using AzureBank.Infrastructure.Data;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace AzureBank.Tests.Unit.HealthChecks;

/// <summary>
/// The readiness budget is COOPERATIVE, so this asserts the half a registration cannot.
/// </summary>
/// <remarks>
/// <para>
/// Two sibling tests already cover the mechanism and its consequence for the checks that exist: a
/// check registered after <c>AddObservability</c> still inherits the bound, and readiness answers
/// inside it against an unroutable store. Neither can cover the next check somebody writes.
/// <c>CancelAfter</c> only SIGNALS a token — nothing abandons a running check, and
/// <c>RunCheckAsync</c> awaits it unconditionally — so a check that never looks at the token it was
/// handed is bounded by NOTHING while its registration still reads as correctly configured.
/// </para>
/// <para>
/// WHAT IT PROVES AND WHAT IT DOES NOT. It proves no registered readiness check can hang when its
/// token is already cancelled, against a store that never answers. It does NOT prove a check
/// "threads its token" — a check that ignores the token but happens to be fast here would pass, and
/// could still be slow against a dependency this host does not have. The property worth having is
/// the endpoint's, not the source's: readiness must come back. That is what is asserted.
/// </para>
/// </remarks>
public class ReadinessCannotHangTests
{
    private readonly ITestOutputHelper _output;

    public ReadinessCannotHangTests(ITestOutputHelper output) => _output = output;

    // RFC 5737 TEST-NET-1: guaranteed unroutable, so a connection attempt HANGS rather than being
    // refused. A refused connection fails fast and would let a token-ignoring check pass.
    private const string UnroutableStore =
        "Server=192.0.2.1;Database=X;User Id=u;Password=p;TrustServerCertificate=True";

    private static ServiceCollection Services()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Development");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:TailTimeoutSeconds"] = "1",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AzureBankDbContext>(o => o.UseSqlServer(UnroutableStore));
        services.AddObservability(environment.Object, configuration);
        return services;
    }

    [Fact]
    public async Task EveryReadinessCheck_ComesBackWhenItsTokenIsAlreadyCancelled()
    {
        using var provider = Services().BuildServiceProvider();
        var registrations = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Where(r => r.Tags.Contains("ready"))
            .ToArray();

        registrations.Should().NotBeEmpty(
            "a guard over an empty set passes forever and proves nothing");

        /*
          THE FIRST ITERATION MEASURED THE PROCESS, NOT THE CHECK. Whichever registration ran first
          paid for the one-time EF model build, and it dominated everything else.

          MEASURED, and the experiment is what makes it a fact rather than a guess: the cost followed
          the POSITION, not the check. Shipped order gave `database 626ms, audit-chain 26ms`; with the
          iteration REVERSED it gave `audit-chain 636ms, database 28ms`. A cost that swaps with the
          order is warm-up by definition. Building the model here first brings the same run to
          `database 68ms, audit-chain 56ms` -- both under 70ms, which is the real cost of a check
          handed an already-cancelled token.

          It matters because of what the assertion means. At 626ms against a 2,000ms bound the guard
          had roughly 3x of headroom over noise that has nothing to do with the property; on a loaded
          agent it would go red for a reason that is not a token-ignoring check, and a guard that
          cries wolf is one people learn to ignore. Warm, the headroom is ~29x.

          Do not delete this as ceremony: without it the first check in the list is measured cold.
        */
        using (var warmup = provider.CreateScope())
        {
            _ = warmup.ServiceProvider.GetRequiredService<AzureBankDbContext>().Model;
        }

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        foreach (var registration in registrations)
        {
            using var scope = provider.CreateScope();
            var check = registration.Factory(scope.ServiceProvider);
            var context = new HealthCheckContext { Registration = registration };

            var stopwatch = Stopwatch.StartNew();
            try
            {
                await check.CheckHealthAsync(context, cancelled.Token);
            }
            catch (OperationCanceledException)
            {
                // Refusing promptly IS honouring the token. Only the elapsed time is the assertion.
            }
            stopwatch.Stop();

            _output.WriteLine($"{registration.Name} -> {stopwatch.ElapsedMilliseconds}ms");

            stopwatch.ElapsedMilliseconds.Should().BeLessThan(
                2_000,
                $"'{registration.Name}' was handed an already-cancelled token against an unroutable "
                + "store and still took that long. The budget only SIGNALS: a check that does not "
                + "look at its token takes /health/ready down with it while its registration reads "
                + "as correctly configured");
        }
    }

    [Fact]
    public void NoReadinessCheckIsRegisteredWhereTheGuardAboveCannotSeeIt()
    {
        /*
          The guard above iterates what AddObservability registers. A readiness check added anywhere
          ELSE -- straight into Program.cs, say -- would never be probed by it, and the coverage
          would look complete while a new check hung the endpoint.

          Asserted by COMPARING TWO HOSTS rather than against a hardcoded list of names. A list is a
          second record and would go stale the first time somebody renamed a check; this cannot,
          because both sides are read from a running container. If the sets diverge, the message
          names the check that escaped.
        */
        using var synthetic = Services().BuildServiceProvider();
        using var real = new CustomWebApplicationFactory();

        var fromExtension = ReadyNames(synthetic);
        using var scope = real.Services.CreateScope();
        var fromHost = ReadyNames(scope.ServiceProvider);

        fromExtension.Should().NotBeEmpty("a comparison of two empty sets passes forever");
        fromHost.Should().BeEquivalentTo(
            fromExtension,
            "every readiness check must be registered by AddObservability, because that is the only "
            + "place the hang guard above looks. One registered elsewhere is invisible to it, and "
            + "the coverage would read as complete while the endpoint could hang again");
    }

    private static string[] ReadyNames(IServiceProvider provider) => provider
        .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
        .Where(r => r.Tags.Contains("ready"))
        .Select(r => r.Name)
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray();
}
