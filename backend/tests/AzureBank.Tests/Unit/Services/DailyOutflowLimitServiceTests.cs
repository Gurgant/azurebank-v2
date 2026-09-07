using AzureBank.Api.Services.Implementations;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// The day's sum, its window and its assertion (ADR-0050 D1–D3), on ONE
/// <see cref="FakeTimeProvider"/> handed to both the context and the service — so a row stamped at
/// 23:59:59Z is on one side of midnight for the writer and the reader alike, and crossing it moves
/// both.
/// </summary>
public class DailyOutflowLimitServiceTests : IDisposable
{
    /// <summary>Mid-day, so a row "today" and a row "yesterday" are unambiguous.</summary>
    private static readonly DateTimeOffset Noon = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Noon);
    private readonly AzureBankDbContext _context;
    private readonly DailyLimitOptions _options = new() { Amount = 500m };
    private readonly DailyOutflowLimitService _sut;

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Account _account;

    public DailyOutflowLimitServiceTests()
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new AzureBankDbContext(options, _clock);
        _sut = new DailyOutflowLimitService(_context, Options.Create(_options), _clock);

        _account = NewAccount(_userId);
        _context.Accounts.Add(_account);
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Account NewAccount(Guid userId, bool isDeleted = false) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        AccountNumber = $"AB-{Random.Shared.Next(1000, 9999)}-{Random.Shared.Next(1000, 9999)}-{Random.Shared.Next(10, 99)}",
        Name = "Daily",
        Type = AccountType.Checking,
        Balance = 0m,
        IsDeleted = isDeleted,
        RowVersion = [0, 0, 0, 0, 0, 0, 0, 1],
        User = null!
    };

    /// <summary>
    /// A ledger row saved through the context, so <c>CreatedAt</c> is the fake clock's instant —
    /// the only way to date a row, since <c>UpdateTimestamps</c> overwrites anything set by hand.
    /// </summary>
    private async Task<Transaction> RowAsync(
        Account account,
        TransactionType type,
        decimal amount,
        string? recipientTag = "someone",
        TransactionStatus status = TransactionStatus.Completed)
    {
        var row = new Transaction
        {
            Id = Guid.CreateVersion7(),
            TransactionNumber = $"TXN-20260907-{Random.Shared.Next(100000, 999999)}",
            AccountId = account.Id,
            Account = account,
            Type = type,
            Amount = amount,
            BalanceBefore = 0m,
            BalanceAfter = 0m,
            RecipientAzureTag = recipientTag,
            Status = status
        };
        _context.Transactions.Add(row);
        await _context.SaveChangesAsync();
        return row;
    }

    private Task<Transaction> ExternalAsync(decimal amount, Account? account = null)
        => RowAsync(account ?? _account, TransactionType.TransferOut, amount);

    // ─── what counts ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnExternalTransferOut_Counts()
    {
        await ExternalAsync(120m);
        await ExternalAsync(30m);

        (await _sut.UsedAsync(_userId)).Should().Be(150m);
    }

    [Fact]
    public async Task AnInternalTransferOut_DoesNotCount_TheTagIsTheDiscriminator()
    {
        // Both transfer kinds write TransferOut; only the external constructor sets the tag
        // (measured on AzureBankDev 2026-09-07: 20 tagged rows to other users, 10 untagged to the
        // same user). An internal row that set a tag would silently start counting — this is the
        // line that would go red.
        await RowAsync(_account, TransactionType.TransferOut, 200m, recipientTag: null);

        (await _sut.UsedAsync(_userId)).Should().Be(0m);
    }

    [Theory]
    [InlineData(TransactionType.Withdrawal)]
    [InlineData(TransactionType.Deposit)]
    [InlineData(TransactionType.TransferIn)]
    public async Task OtherMovements_DoNotCount(TransactionType type)
    {
        await RowAsync(_account, type, 200m);

        (await _sut.UsedAsync(_userId)).Should().Be(0m, $"{type} is not an outgoing external transfer");
    }

    [Fact]
    public async Task ARowThatIsNotCompleted_DoesNotCount()
    {
        // Nothing writes Pending today (measured: "statuses ever written in the table: Completed
        // 207"), so this pins the rule a future Pending writer must revisit, not a live path.
        await RowAsync(_account, TransactionType.TransferOut, 200m, status: TransactionStatus.Pending);

        (await _sut.UsedAsync(_userId)).Should().Be(0m);
    }

    [Fact]
    public async Task AnotherUsersExternalTransfer_DoesNotCount()
    {
        var other = NewAccount(Guid.NewGuid());
        _context.Accounts.Add(other);
        await _context.SaveChangesAsync();
        await ExternalAsync(300m, other);

        (await _sut.UsedAsync(_userId)).Should().Be(0m, "the limit is per payer, joined through Account.UserId");
    }

    [Fact]
    public async Task ARowOnAClosedAccountOfTheSameUser_StillCounts()
    {
        /*
          Closing needs a zero balance (NON_ZERO_BALANCE), so an account drained by counted
          outflows and then closed carried real money out today. Copying GetSummaryAsync's
          !IsDeleted filter would hand the day's headroom back on closure.
        */
        var closed = NewAccount(_userId, isDeleted: true);
        _context.Accounts.Add(closed);
        await _context.SaveChangesAsync();
        await ExternalAsync(250m, closed);
        await ExternalAsync(100m);

        (await _sut.UsedAsync(_userId)).Should().Be(350m);

        // MEASURED, and the reason IgnoreQueryFilters() is on the query rather than decoration:
        // the same predicate WITHOUT it loses the closed account's row, because the global
        // soft-delete filter on Account applies through the Transaction.Account navigation.
        var withTheFilter = await _context.Transactions.AsNoTracking()
            .Where(t => t.Account.UserId == _userId && t.Type == TransactionType.TransferOut
                        && t.RecipientAzureTag != null)
            .SumAsync(t => t.Amount);
        withTheFilter.Should().Be(100m, "without IgnoreQueryFilters the closed account's outflow vanishes");
    }

    // ─── the window ────────────────────────────────────────────────────────────

    [Fact]
    public void TheWindow_IsTheUtcCalendarDay_AndResetsAtTheNextMidnight()
    {
        var (start, resetsAt) = _sut.Window();

        start.Should().Be(new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc));
        resetsAt.Should().Be(new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc));
        start.Kind.Should().Be(DateTimeKind.Utc);
        resetsAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task ARowAtTheLastSecondOfTheDay_Counts_UntilTheOneClockCrossesMidnight()
    {
        _clock.SetUtcNow(new DateTimeOffset(2026, 9, 7, 23, 59, 59, TimeSpan.Zero));
        await ExternalAsync(400m);

        (await _sut.UsedAsync(_userId)).Should().Be(400m, "23:59:59Z is still the 7th");

        // ONE clock moves, and both the row's day and the window's day are read from it.
        _clock.SetUtcNow(new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero));

        (await _sut.UsedAsync(_userId)).Should().Be(0m, "at 00:00:00Z the day has rolled and the row is yesterday's");
        _sut.Window().ResetsAt.Should().Be(new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task YesterdaysRow_DoesNotCount()
    {
        // A FakeTimeProvider only moves forward, so "yesterday" is written first and the clock
        // then advances a whole day past it.
        await ExternalAsync(499m);
        _clock.SetUtcNow(Noon.AddDays(1));

        (await _sut.UsedAsync(_userId)).Should().Be(0m, "a row from the previous UTC day is not today's");
    }

    // ─── the assertion ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExactlyTheLimit_Passes_AndOneCentOver_RefusesWithTheExactDetails()
    {
        await ExternalAsync(300m);

        await _sut.Invoking(s => s.AssertCanMoveAsync(_userId, 200m))
            .Should().NotThrowAsync("used + requested == limit is allowed: the bound is inclusive");

        var refusal = await _sut.Invoking(s => s.AssertCanMoveAsync(_userId, 200.01m))
            .Should().ThrowAsync<DailyLimitExceededException>();

        refusal.Which.Details.Should().BeEquivalentTo(new Dictionary<string, object>
        {
            ["limit"] = 500m,
            ["used"] = 300m,
            ["requested"] = 200.01m,
            ["resetsAt"] = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
        });
        refusal.Which.Message.Should().Be("Daily transfer limit exceeded.");
    }

    [Fact]
    public async Task WithNothingSpent_ARequestAboveTheLimit_IsRefusedOnItsOwn()
    {
        var refusal = await _sut.Invoking(s => s.AssertCanMoveAsync(_userId, 500.01m))
            .Should().ThrowAsync<DailyLimitExceededException>();

        refusal.Which.Details!["used"].Should().Be(0m);
    }

    [Fact]
    public void TheLimit_IsTheBoundValue()
    {
        _sut.Limit.Should().Be(500m);
    }
}
