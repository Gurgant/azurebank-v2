using AzureBank.Api.Handlers;
using AzureBank.Api.Services;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Update;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// How a conflict is classified by the entries it names (<see cref="ConcurrencyRetry.IsAccountRace"/>):
/// one that names accounts and nothing else may be tried again, and one that names the idempotency
/// record may not.
/// </summary>
/// <remarks>
/// The exceptions are built by hand, from entries of a context, so they hold what the test gives
/// them. What SQL Server and EF name is printed by <c>BalanceConcurrencySqlServerTests</c>: the
/// Account alone for a save that lost its account, also when the same save lost its record, and the
/// record for a save that lost only its claim. Whether a deposit past its attempts answers a busy
/// account is another question, asked of the store: <c>ConcurrencyRetryBusyAnswerTests</c>.
/// </remarks>
public sealed class ConcurrencyRetryAccountRaceTests : IDisposable
{
    private readonly AzureBankDbContext _context = new(
        new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public void Dispose() => _context.Dispose();

    [Fact]
    public void AConflictOnAccountsOnly_IsAnAccountRace()
    {
        ConcurrencyRetry.IsAccountRace(Conflict(Account())).Should().BeTrue();
        ConcurrencyRetry.IsAccountRace(Conflict(Account(), Account())).Should().BeTrue(
            "a transfer's save can lose both of its accounts");
    }

    [Fact]
    public void AConflictThatNamesTheIdempotencyRecord_IsNotAnAccountRace()
    {
        ConcurrencyRetry.IsAccountRace(Conflict(Record())).Should().BeFalse(
            "the claim was taken over: another request may hold the key");
    }

    [Fact]
    public void AConflictBuiltByHandToNameAnAccountAndTheRecord_IsNotAnAccountRace()
    {
        // No save measured on SQL Server names both: a deposit's save that loses both rows names
        // the Account alone. The case holds the rule as it is written, every entry an Account, for
        // a conflict that would name more.
        ConcurrencyRetry.IsAccountRace(Conflict(Account(), Record())).Should().BeFalse(
            "one named record is enough, whatever else is named");
    }

    [Fact]
    public void AConflictThatNamesNoEntry_IsNotAnAccountRace()
    {
        ConcurrencyRetry.IsAccountRace(new DbUpdateConcurrencyException("stale")).Should().BeFalse(
            "nothing says the account is what was lost");
    }

    [Fact]
    public void OnlyAnAccountRaceIsTriedAgain_AndOnlyWhileAnAttemptIsLeft()
    {
        var race = Conflict(Account());
        var fencedOut = Conflict(Record());

        ConcurrencyRetry.ShouldRetry(race, attempt: 1).Should().BeTrue();
        ConcurrencyRetry.ShouldRetry(race, ConcurrencyRetry.MaxAttempts - 1).Should().BeTrue();
        ConcurrencyRetry.ShouldRetry(race, ConcurrencyRetry.MaxAttempts).Should().BeFalse("the last attempt is the last");
        ConcurrencyRetry.ShouldRetry(fencedOut, attempt: 1).Should().BeFalse(
            "running again a request whose claim was taken over could move the money twice");
    }

    [Fact]
    public void AConflictThatNamesTheIdempotencyRecord_IsClassifiedAsNoAccountRace_AndBareItIsNo503()
    {
        // A classification, of one conflict: the deposit raises AccountBusyException only on a
        // conflict this calls an account race, so one that names the record reaches the handlers
        // bare, and bare it has no reason to be a 503.
        var fencedOut = Conflict(Record());

        ConcurrencyRetry.IsAccountRace(fencedOut).Should().BeFalse();
        ServiceUnavailableExceptionHandler.ReasonFor(fencedOut, firedByDeadline: false).Should().BeNull();
        ServiceUnavailableExceptionHandler.ReasonFor(new AccountBusyException(Conflict(Account())), firedByDeadline: false)
            .Should().Be(ServiceUnavailableExceptionHandler.AccountBusy);
    }

    private DbUpdateConcurrencyException Conflict(params object[] refused)
    {
        // EF1001: the entry a save refuses is EF's internal one, and the exception's public
        // constructor takes nothing else.
#pragma warning disable EF1001
        IReadOnlyList<IUpdateEntry> entries = [.. refused.Select(e => _context.Entry(e).GetInfrastructure())];
#pragma warning restore EF1001
        return new DbUpdateConcurrencyException("stale", entries);
    }

    private static Account Account() => new()
    {
        Id = Guid.NewGuid(),
        AccountNumber = "AZ0000000000",
        Name = "Main",
        User = null!,
    };

    private static IdempotencyRecord Record() => new()
    {
        UserId = Guid.NewGuid(),
        Endpoint = "POST api/transactions/deposit",
        Key = Guid.NewGuid(),
        ClaimId = Guid.NewGuid(),
        RequestHash = new string('a', 64),
    };
}
