using AzureBank.Api.Middleware;
using AzureBank.Api.Services;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// What a write past its attempts must be able to show before it answers a busy account
/// (<see cref="ConcurrencyRetry.ClaimIsOwnAndNothingCommittedAsync"/>): the record under its key is
/// still its own claim and still <c>Processing</c>, and the request has let no commit start.
/// </summary>
/// <remarks>
/// One case per test the method makes, each on a record another context changed, as another
/// request would. <c>BalanceConcurrencySqlServerTests</c> makes two of them on SQL Server through
/// the API: a claim taken over, and a commit that landed.
/// </remarks>
public sealed class ConcurrencyRetryBusyAnswerTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _request;
    private readonly AzureBankDbContext _context;
    private readonly RequestDeadline _deadline = new(
        new FakeTimeProvider(), TimeSpan.FromSeconds(40), CancellationToken.None, NullLogger.Instance);

    public ConcurrencyRetryBusyAnswerTests()
    {
        var database = Guid.NewGuid().ToString();
        _provider = new ServiceCollection()
            .AddScoped<RequestDeadlineScope>()
            .AddDbContext<AzureBankDbContext>(options => options.UseInMemoryDatabase(database))
            .BuildServiceProvider();
        _request = _provider.CreateScope();
        _request.ServiceProvider.GetRequiredService<RequestDeadlineScope>().Deadline = _deadline;
        _context = _request.ServiceProvider.GetRequiredService<AzureBankDbContext>();
    }

    public void Dispose()
    {
        _deadline.Dispose();
        _request.Dispose();
        _provider.Dispose();
    }

    [Fact]
    public async Task AClaimThatIsStillItsOwn_AndNoCommit_MayBeAnsweredBusy()
    {
        await ClaimAsync();

        (await AskAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task ARecordThatIsGone_IsNotABusyAccount()
    {
        var record = await ClaimAsync();
        await InTheStoreAsync(record, (db, stored) => db.Remove(stored));

        (await AskAsync()).Should().BeFalse("the claim was taken over or cleaned: the key is not this request's");
    }

    [Fact]
    public async Task ARecordThatCarriesAnotherClaim_IsNotABusyAccount()
    {
        var record = await ClaimAsync();
        await InTheStoreAsync(record, (_, stored) => stored.ClaimId = Guid.NewGuid());

        (await AskAsync()).Should().BeFalse("another request holds the key");
    }

    [Theory]
    [InlineData(IdempotencyStatus.Executed)]
    [InlineData(IdempotencyStatus.Completed)]
    public async Task ARecordThatRodeACommit_IsNotABusyAccount(IdempotencyStatus status)
    {
        // The claim is left as the request made it, so that the status alone decides.
        var record = await ClaimAsync();
        await InTheStoreAsync(record, (_, stored) => stored.Status = status);

        (await AskAsync()).Should().BeFalse("a commit under this key landed");
    }

    [Fact]
    public async Task ARequestThatLetACommitStart_IsNotABusyAccount()
    {
        await ClaimAsync();
        _deadline.TryEnterCommit().Should().BeTrue();
        _deadline.CommitFailed();

        (await AskAsync()).Should().BeFalse("a commit that started may have landed, whatever the record says now");
    }

    [Fact]
    public async Task ARequestWithoutADeadline_IsNotABusyAccount()
    {
        await ClaimAsync();
        _request.ServiceProvider.GetRequiredService<RequestDeadlineScope>().Deadline = null;

        (await AskAsync()).Should().BeFalse("nothing says whether a commit started");
    }

    [Fact]
    public async Task AWriteThatHoldsNoRecord_IsNotABusyAccount()
    {
        (await AskAsync()).Should().BeFalse("no claim shows that the same key can be sent again");
    }

    private Task<bool> AskAsync() =>
        ConcurrencyRetry.ClaimIsOwnAndNothingCommittedAsync(_context, CancellationToken.None);

    /// <summary>
    /// Claims a key as the middleware does: the record is inserted <c>Processing</c>, then marked
    /// <c>Executed</c> under a new <c>ClaimId</c> without a save, for the write's own save to carry.
    /// </summary>
    private async Task<IdempotencyRecord> ClaimAsync()
    {
        var record = new IdempotencyRecord
        {
            UserId = Guid.NewGuid(),
            Endpoint = "POST api/transactions/deposit",
            Key = Guid.NewGuid(),
            ClaimId = Guid.NewGuid(),
            RequestHash = new string('a', 64),
            Status = IdempotencyStatus.Processing,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24),
        };
        _context.IdempotencyRecords.Add(record);
        await _context.SaveChangesAsync();

        record.Status = IdempotencyStatus.Executed;
        record.ClaimId = Guid.NewGuid();
        return record;
    }

    /// <summary>Changes the stored record from a context of another scope, as another request does.</summary>
    private async Task InTheStoreAsync(IdempotencyRecord record, Action<AzureBankDbContext, IdempotencyRecord> change)
    {
        using var other = _provider.CreateScope();
        var db = other.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var stored = await db.IdempotencyRecords.SingleAsync(
            r => r.UserId == record.UserId && r.Endpoint == record.Endpoint && r.Key == record.Key);
        change(db, stored);
        await db.SaveChangesAsync();
    }
}
