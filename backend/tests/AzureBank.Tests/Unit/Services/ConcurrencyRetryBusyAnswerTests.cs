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
/// What a write past its attempts must be able to show before it answers a busy account. A write
/// that holds an idempotency key (<see cref="ConcurrencyRetry.ClaimIsOwnAndNothingCommittedAsync"/>):
/// the request has let no commit start, and the record under its key is still its own claim and
/// still <c>Processing</c>. A write that holds none (<see cref="ConcurrencyRetry.NoCommitWasLetStart"/>):
/// the request has let no commit start.
/// </summary>
/// <remarks>
/// <para>
/// One case per test each half makes, on the provider that half can be asked on without a
/// database. The record's half (<see cref="ConcurrencyRetry.ClaimIsStillOwnAsync"/>) reads the
/// store, so it is asked on the in-memory provider, where another context changes the record as
/// another request would. The mark's half is a proof only on a relational provider, so it is asked
/// on a SQL Server context that never opens a connection: the mark is read from the request's
/// scope, not from the store.
/// </para>
/// <para>
/// The two halves together are asked through the API on SQL Server.
/// <c>BalanceConcurrencySqlServerTests</c> makes two of the cases for a deposit: a claim taken
/// over, and a commit that landed. <c>BusyMoneyWriteSqlServerTests</c> makes two for a withdrawal
/// and both transfers: a claim taken over, and a commit that was let start and failed. Its proof
/// of a commit that landed is answered by the preparation of the attempt that runs again, before
/// either half is asked.
/// </para>
/// </remarks>
public sealed class ConcurrencyRetryBusyAnswerTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _request;
    private readonly AzureBankDbContext _context;
    private readonly ServiceProvider _relationalProvider;
    private readonly IServiceScope _relationalRequest;
    private readonly AzureBankDbContext _relational;
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

        // No test opens a connection on this context, so the server it names is never looked for.
        _relationalProvider = new ServiceCollection()
            .AddScoped<RequestDeadlineScope>()
            .AddDbContext<AzureBankDbContext>(options => options.UseSqlServer(
                "Server=no-such-server;Database=never-opened;Connect Timeout=1"))
            .BuildServiceProvider();
        _relationalRequest = _relationalProvider.CreateScope();
        _relationalRequest.ServiceProvider.GetRequiredService<RequestDeadlineScope>().Deadline = _deadline;
        _relational = _relationalRequest.ServiceProvider.GetRequiredService<AzureBankDbContext>();
    }

    public void Dispose()
    {
        _deadline.Dispose();
        _request.Dispose();
        _provider.Dispose();
        _relationalRequest.Dispose();
        _relationalProvider.Dispose();
    }

    // The record's half.

    [Fact]
    public async Task AClaimThatIsStillItsOwn_IsTheRequestsOwn()
    {
        await ClaimAsync();

        (await AskTheRecordAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task ARecordThatIsGone_IsNotABusyAccount()
    {
        var record = await ClaimAsync();
        await InTheStoreAsync(record, (db, stored) => db.Remove(stored));

        (await AskTheRecordAsync()).Should().BeFalse("the claim was taken over or cleaned: the key is not this request's");
    }

    [Fact]
    public async Task ARecordThatCarriesAnotherClaim_IsNotABusyAccount()
    {
        var record = await ClaimAsync();
        await InTheStoreAsync(record, (_, stored) => stored.ClaimId = Guid.NewGuid());

        (await AskTheRecordAsync()).Should().BeFalse("another request holds the key");
    }

    [Theory]
    [InlineData(IdempotencyStatus.Executed)]
    [InlineData(IdempotencyStatus.Completed)]
    public async Task ARecordThatRodeACommit_IsNotABusyAccount(IdempotencyStatus status)
    {
        // The claim is left as the request made it, so that the status alone decides.
        var record = await ClaimAsync();
        await InTheStoreAsync(record, (_, stored) => stored.Status = status);

        (await AskTheRecordAsync()).Should().BeFalse("a commit under this key landed");
    }

    [Fact]
    public async Task AWriteThatHoldsNoRecord_IsNotABusyAccount()
    {
        (await AskTheRecordAsync()).Should().BeFalse("no claim shows that the same key can be sent again");
    }

    // The mark's half.

    [Fact]
    public void ARequestThatLetNoCommitStart_MayBeAnsweredBusy()
    {
        ConcurrencyRetry.NoCommitWasLetStart(_relational).Should().BeTrue(
            "the mark is the proof of a write that holds no claim, and no record is asked for");
    }

    [Fact]
    public void ARequestThatLetACommitStart_IsNotABusyAccount()
    {
        _deadline.TryEnterCommit().Should().BeTrue();
        _deadline.CommitFailed();

        ConcurrencyRetry.NoCommitWasLetStart(_relational).Should().BeFalse(
            "a commit that started may have landed, also when it failed");
    }

    [Fact]
    public void ARequestThatLetACommitLand_IsNotABusyAccount()
    {
        _deadline.TryEnterCommit().Should().BeTrue();
        _deadline.Committed();

        ConcurrencyRetry.NoCommitWasLetStart(_relational).Should().BeFalse();
    }

    [Fact]
    public void ARequestWithoutADeadline_IsNotABusyAccount()
    {
        _relationalRequest.ServiceProvider.GetRequiredService<RequestDeadlineScope>().Deadline = null;

        ConcurrencyRetry.NoCommitWasLetStart(_relational).Should().BeFalse("nothing says whether a commit started");
    }

    /// <summary>
    /// A provider that is not relational has no transactions: each save lands at once and no gate
    /// marks it, so an unset mark shows nothing there.
    /// </summary>
    [Fact]
    public void OnAProviderWithoutTransactions_AnUnsetMarkIsNoProof()
    {
        ConcurrencyRetry.NoCommitWasLetStart(_context).Should().BeFalse(
            "a save on this provider lands without a commit the gate could mark");
    }

    // The two halves together.

    /// <summary>
    /// The mark is asked first, and with it set the record is not read: this context has no server
    /// to read from, and the answer is no all the same.
    /// </summary>
    [Fact]
    public async Task AKeyedWriteThatLetACommitStart_IsNotABusyAccount_AndItsRecordIsNotRead()
    {
        _relational.IdempotencyRecords.Attach(NewClaim());
        _deadline.TryEnterCommit().Should().BeTrue();
        _deadline.CommitFailed();

        (await ConcurrencyRetry.ClaimIsOwnAndNothingCommittedAsync(_relational, CancellationToken.None))
            .Should().BeFalse("a commit that started may have landed, whatever the record says now");
    }

    [Fact]
    public async Task OnAProviderWithoutTransactions_AKeyedWriteIsNotABusyAccount_WhateverItsRecordSays()
    {
        await ClaimAsync();
        (await AskTheRecordAsync()).Should().BeTrue("the proof is void unless the record alone would say yes");

        (await ConcurrencyRetry.ClaimIsOwnAndNothingCommittedAsync(_context, CancellationToken.None))
            .Should().BeFalse("the mark's half says no on this provider");
    }

    private Task<bool> AskTheRecordAsync() =>
        ConcurrencyRetry.ClaimIsStillOwnAsync(_context, CancellationToken.None);

    private static IdempotencyRecord NewClaim() => new()
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

    /// <summary>
    /// Claims a key as the middleware does: the record is inserted <c>Processing</c>, then marked
    /// <c>Executed</c> under a new <c>ClaimId</c> without a save, for the write's own save to carry.
    /// </summary>
    private async Task<IdempotencyRecord> ClaimAsync()
    {
        var record = NewClaim();
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
