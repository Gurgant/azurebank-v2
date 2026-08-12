using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// One clock read per save.
///
/// <para>
/// <c>UpdateTimestamps</c> used to call <c>DateTime.UtcNow</c> six times, and two of those cost
/// real accuracy. <c>CreatedAt == UpdatedAt</c> on an insert held only because two reads a few
/// instructions apart usually land in the same tick — true by granularity, not by construction.
/// Worse, the read for <c>Transaction</c> sat INSIDE its loop, so the two legs of a transfer —
/// one event, one <c>SaveChanges</c>, one database transaction — were stamped with two independent
/// instants. Anything reconstructing a transfer from its rows would see two events milliseconds
/// apart, which is precisely the kind of lie an audit trail must not tell.
/// </para>
///
/// <para>
/// These assert EXACT equality against a <c>FakeTimeProvider</c>, so they cannot pass by luck: a
/// second clock read would return a different instant, because the fake only moves when told.
/// </para>
/// </summary>
public class AzureBankDbContextTimestampTests
{
    /// <summary>An instant with a non-zero millisecond, so a truncating bug cannot hide.</summary>
    private static readonly DateTimeOffset Instant =
        new(2026, 8, 12, 10, 30, 45, 123, TimeSpan.Zero);

    private static AzureBankDbContext NewContext(TimeProvider clock) =>
        new(new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options, clock);

    private static Transaction NewTransaction() => new()
    {
        Id = Guid.CreateVersion7(),
        TransactionNumber = $"TXN-20260812-{Random.Shared.Next(100000, 999999)}",
        AccountId = Guid.CreateVersion7(),
        Type = TransactionType.Deposit,
        Amount = 100m,
        BalanceBefore = 0m,
        BalanceAfter = 100m,
        Status = TransactionStatus.Completed,
        Account = null! // FK not enforced on InMemory; nav not needed here
    };

    private static Account NewAccount() => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = Guid.CreateVersion7(),
        AccountNumber = "AB-1111-2222-33",
        Name = "Timestamps",
        Type = AccountType.Checking,
        Balance = 0m,
        // SQL Server generates this; InMemory only enforces that it is not null.
        RowVersion = [0, 0, 0, 0, 0, 0, 0, 1],
        User = null! // FK not enforced on InMemory
    };

    [Fact]
    public void TwoTransactionsInOneSave_ShareTheSameCreatedAt()
    {
        // THE ONE THAT MATTERS. A transfer inserts its two legs in a single SaveChanges. Before the
        // fix the clock was read per row, so this asserted-equal pair could differ.
        var clock = new FakeTimeProvider(Instant);
        using var ctx = NewContext(clock);

        var outgoing = NewTransaction();
        var incoming = NewTransaction();
        ctx.Transactions.AddRange(outgoing, incoming);
        ctx.SaveChanges();

        outgoing.CreatedAt.Should().Be(incoming.CreatedAt,
            "two legs of one transfer are one event and must carry one instant");
        outgoing.CreatedAt.Should().Be(Instant.UtcDateTime,
            "the stamp comes from the injected clock, not from DateTime.UtcNow");
    }

    [Fact]
    public void InsertedEntity_HasCreatedAtExactlyEqualToUpdatedAt()
    {
        var clock = new FakeTimeProvider(Instant);
        using var ctx = NewContext(clock);

        var account = NewAccount();
        ctx.Accounts.Add(account);
        ctx.SaveChanges();

        account.CreatedAt.Should().Be(account.UpdatedAt,
            "an insert is one event; equality here must hold by construction, not by clock granularity");
        account.CreatedAt.Should().Be(Instant.UtcDateTime);
    }

    [Fact]
    public void ASecondSave_StampsTheLaterInstant_AndLeavesCreatedAtAlone()
    {
        // The counterpart to the tests above: pinning ONE instant per save must not freeze the
        // clock across saves, or the fix would have replaced a precision bug with a staleness one.
        var clock = new FakeTimeProvider(Instant);
        using var ctx = NewContext(clock);

        var account = NewAccount();
        ctx.Accounts.Add(account);
        ctx.SaveChanges();
        var createdAt = account.CreatedAt;

        clock.Advance(TimeSpan.FromMinutes(5));
        account.Name = "Renamed";
        ctx.SaveChanges();

        account.CreatedAt.Should().Be(createdAt, "CreatedAt is written once, on insert");
        account.UpdatedAt.Should().Be(Instant.AddMinutes(5).UtcDateTime);
    }

    [Fact]
    public void EveryEntityInOneSave_SharesTheSameInstant_AcrossAllThreeWalks()
    {
        // UpdateTimestamps walks BaseEntity, Transaction and ApplicationUser separately. One save
        // is one event, so the three walks must agree — they used to read the clock independently.
        var clock = new FakeTimeProvider(Instant);
        using var ctx = NewContext(clock);

        var account = NewAccount();
        var transaction = NewTransaction();
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            AzureTag = "stamp_probe",
            FirstName = "Stamp",
            LastName = "Probe",
            UserName = "stamp_probe",
            Email = "stamp@example.com"
        };

        ctx.Accounts.Add(account);
        ctx.Transactions.Add(transaction);
        ctx.Users.Add(user);
        ctx.SaveChanges();

        account.CreatedAt.Should().Be(Instant.UtcDateTime);
        transaction.CreatedAt.Should().Be(Instant.UtcDateTime);
        user.CreatedAt.Should().Be(Instant.UtcDateTime);
    }
}
