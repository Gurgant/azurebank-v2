using AzureBank.Api.HealthChecks;
using AzureBank.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using AzureBank.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace AzureBank.Tests.Unit.HealthChecks;

/// <summary>
/// The readiness probe that tells an operator the bank cannot move money before a customer does.
/// </summary>
/// <remarks>
/// Since ADR-0044 D1 an unreachable audit store stops every deposit, withdrawal and transfer. A
/// health check that could only ever say "healthy" would be worse than none, because it would look
/// like coverage — so the unhealthy direction is what these pin.
/// </remarks>
public class AuditChainHealthCheckTests
{
    [Fact]
    public void TheCheckIsActuallyREGISTERED_AndTaggedReady()
    {
        /*
          "The class exists" and "the host runs it" are different claims, and only the second one is
          the feature. A readiness probe that was written but never registered reports Healthy
          forever — indistinguishable from a working one, and worse than none, because it looks like
          coverage.

          The API log cannot settle this: it does not print the probe SQL, so an absent line is not
          evidence of an absent check. Asking the host what it registered is.
        */
        using var factory = new CustomWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        var options = scope.ServiceProvider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        var audit = options.Registrations.SingleOrDefault(r => r.Name == "audit-chain");

        audit.Should().NotBeNull(
            "an unregistered readiness probe reports nothing and looks exactly like a passing one");
        audit!.Tags.Should().Contain(
            "ready",
            "readiness is the signal that takes an instance out of rotation — since ADR-0044 D1 an "
            + "instance that cannot reach the audit store cannot move money, so it should be taken out");
    }

    private static AuditChainHealthCheck ForConnection(string connectionString) =>
        new(new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseSqlServer(connectionString)
                .Options));

    [Fact]
    public async Task WhenTheAuditStoreCannotBeReached_ItReportsUnhealthy()
    {
        /*
          A SQL Server provider pointed at a server that is not there. No database is needed and none
          is touched — the point is that the probe FAILS, and a connection that cannot open fails it
          for the same reason a missing table would.

          Unhealthy rather than Degraded is the assertion that matters: "degraded" would leave this
          instance in a load balancer's rotation, still accepting money movements it cannot audit and
          therefore cannot complete.
        */
        var check = ForConnection(
            "Server=localhost,1;Database=NoSuchDatabase;User Id=nobody;Password=nothing;"
            + "TrustServerCertificate=True;Connect Timeout=1");

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(
            HealthStatus.Unhealthy,
            "an instance that cannot reach the audit store cannot move money, so it is not serving a "
            + "reduced service — it is not serving the one that matters");
        result.Description.Should().Contain(
            "money movements will be refused",
            "the operator reading this must not have to know ADR-0044 by heart to understand it");
        result.Exception.Should().NotBeNull("the cause is what a runbook starts from");
    }

    [Fact]
    public async Task OnANonRelationalProvider_ItReportsHealthyRatherThanPretendingToProbe()
    {
        // The negative control. The InMemory provider has no SQL to send and no store to be
        // unreachable; reporting healthy is honest, and reporting anything else would make the
        // ~585 InMemory tests fail for a reason that has nothing to do with them.
        var check = new AuditChainHealthCheck(new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("not applicable");
    }
}
