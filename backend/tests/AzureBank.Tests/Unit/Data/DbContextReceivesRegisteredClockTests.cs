using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// The context's optional <see cref="TimeProvider"/> parameter actually RECEIVES the registered
/// clock when the context is resolved from DI (ADR-0050, Phase 0.3).
/// </summary>
/// <remarks>
/// <para>
/// Until this test the claim was inferred: the registration comment on <c>IAuditChain</c> says
/// <c>AddDbContext</c> resolves the constructor from DI, and <c>IAuditChain</c> demonstrably
/// arrives. But an optional parameter with a default is a different case from a required one, and
/// if it were NOT bound the day's window and the ledger's <c>CreatedAt</c> would come from two
/// clocks — and the FakeTimeProvider day-boundary tests would prove nothing while passing.
/// </para>
/// <para>
/// Expected from the code, then measured here: <c>ActivatorUtilities</c> resolves an optional
/// parameter from the container once the type is registered, and falls back to the default only
/// when it is not.
/// </para>
/// </remarks>
public class DbContextReceivesRegisteredClockTests
{
    /// <summary>An instant no wall clock will report, with a non-zero millisecond.</summary>
    private static readonly DateTimeOffset Instant = new(2031, 3, 4, 5, 6, 7, 890, TimeSpan.Zero);

    private static Transaction NewTransaction() => new()
    {
        Id = Guid.CreateVersion7(),
        TransactionNumber = $"TXN-20310304-{Random.Shared.Next(100000, 999999)}",
        AccountId = Guid.CreateVersion7(),
        Type = TransactionType.Deposit,
        Amount = 1m,
        BalanceBefore = 0m,
        BalanceAfter = 1m,
        Status = TransactionStatus.Completed,
        Account = null! // FK not enforced on InMemory
    };

    private static ServiceCollection WithContext()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AzureBankDbContext>(o =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        return services;
    }

    [Fact]
    public void WhenATimeProviderIsRegistered_TheContextStampsWithIt()
    {
        var services = WithContext();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Instant));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var row = NewTransaction();
        db.Transactions.Add(row);
        db.SaveChanges();

        row.CreatedAt.Should().Be(
            Instant.UtcDateTime,
            "AddDbContext resolved the optional TimeProvider parameter from the container");
    }

    [Fact]
    public void WhenNoneIsRegistered_TheContextFallsBackToTheSystemClock()
    {
        // The control for the test above: the same registration minus the clock stamps near the
        // real now, so the instant in the first test can only have come through the parameter.
        var services = WithContext();
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var row = NewTransaction();
        db.Transactions.Add(row);
        db.SaveChanges();

        row.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        row.CreatedAt.Should().NotBe(Instant.UtcDateTime);
    }

    [Fact]
    public void ThroughTheCompositionRoot_TheFactorysFakeClock_ReachesTheContext()
    {
        // The swap CustomWebApplicationFactory offers, proven on the real host: the day-boundary
        // integration test stands on this and would be silent if the swap did not reach the stamp.
        using var factory = new CustomWebApplicationFactory();
        factory.UseFakeClock(Instant);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var row = NewTransaction();
        db.Transactions.Add(row);
        db.SaveChanges();

        row.CreatedAt.Should().Be(Instant.UtcDateTime);
        scope.ServiceProvider.GetRequiredService<TimeProvider>().Should().BeSameAs(factory.Clock);
    }
}
