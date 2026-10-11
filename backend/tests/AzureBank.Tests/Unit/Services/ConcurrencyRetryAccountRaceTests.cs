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
/// What counts as a race for an account (<see cref="ConcurrencyRetry.IsAccountRace"/>): the one
/// conflict a write may try again, and the one a deposit past its attempts answers as a busy
/// account. A conflict that involves the idempotency record is neither.
/// </summary>
/// <remarks>
/// The exceptions are built as EF builds them, from the entries a save refused. SQL Server makes
/// the real ones in <c>BalanceConcurrencySqlServerTests</c>: an account's row lost on every
/// attempt, and a claim taken away at the save.
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
    public void AConflictThatInvolvesTheIdempotencyRecord_IsNotAnAccountRace()
    {
        ConcurrencyRetry.IsAccountRace(Conflict(Record())).Should().BeFalse(
            "the claim was taken over: another request may hold the key");
        ConcurrencyRetry.IsAccountRace(Conflict(Account(), Record())).Should().BeFalse(
            "one refused record is enough, whatever else the save lost");
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
        var fencedOut = Conflict(Account(), Record());

        ConcurrencyRetry.ShouldRetry(race, attempt: 1).Should().BeTrue();
        ConcurrencyRetry.ShouldRetry(race, ConcurrencyRetry.MaxAttempts - 1).Should().BeTrue();
        ConcurrencyRetry.ShouldRetry(race, ConcurrencyRetry.MaxAttempts).Should().BeFalse("the last attempt is the last");
        ConcurrencyRetry.ShouldRetry(fencedOut, attempt: 1).Should().BeFalse(
            "running again a request whose claim was taken over could move the money twice");
    }

    [Fact]
    public void AConflictThatInvolvesTheIdempotencyRecord_IsNeverAnsweredSendItAgain()
    {
        // The deposit raises AccountBusyException only for an account race, so the conflict of a
        // request whose claim was taken over reaches the handlers bare. Bare, it is no 503: no
        // Retry-After and no applied, at the first attempt as at the last.
        var fencedOut = Conflict(Account(), Record());

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
