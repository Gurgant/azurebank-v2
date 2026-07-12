using System.Text;
using AzureBank.Api.Services.Implementations;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for IdempotencyService (ADR-0009): fingerprinting, the
/// claim/decision state machine, ClaimId fencing and release semantics.
/// </summary>
public class IdempotencyServiceTests : IDisposable
{
    private const string Endpoint = "POST api/transfers";
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly InMemoryDatabaseRoot _root = new();
    private readonly ServiceProvider _provider;
    private readonly AzureBankDbContext _context;
    private readonly IdempotencyService _sut;

    private static IdempotencyOptions NewOptions() => new()
    {
        HashKey = "unit-tests-only-idempotency-hmac-key-0123456789",
        Ttl = TimeSpan.FromHours(24),
        ProcessingStaleAfter = TimeSpan.FromMinutes(10),
        CleanupInterval = TimeSpan.FromHours(1)
    };

    public IdempotencyServiceTests()
    {
        // A scope factory over the SAME store: ReleaseIfNotExecutedAsync
        // deliberately opens its own scope (the request context may hold
        // failed business changes).
        var services = new ServiceCollection();
        services.AddDbContext<AzureBankDbContext>(o => o.UseInMemoryDatabase(_dbName, _root));
        _provider = services.BuildServiceProvider();

        _context = NewContext();
        _sut = CreateService(_context);
    }

    private IdempotencyService CreateService(AzureBankDbContext context, IdempotencyOptions? options = null) =>
        new(context,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options ?? NewOptions()),
            Mock.Of<ILogger<IdempotencyService>>());

    private AzureBankDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(_dbName, _root)
            .Options);

    public void Dispose()
    {
        _context.Dispose();
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Endpoint identity & fingerprinting

    [Theory]
    [InlineData("post", "api/transfers", "POST api/transfers")]
    [InlineData("POST", "api/transactions/deposit", "POST api/transactions/deposit")]
    public void EndpointNameFor_UsesUppercaseMethodAndRoutePattern(
        string method, string pattern, string expected)
    {
        IdempotencyService.EndpointNameFor(method, pattern).Should().Be(expected);
    }

    [Fact]
    public async Task ComputeRequestHash_IsDeterministic_LowercaseHex64()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"amount":50}""");

        var first = await _sut.ComputeRequestHashAsync(new MemoryStream(bytes), CancellationToken.None);
        var second = await _sut.ComputeRequestHashAsync(new MemoryStream(bytes), CancellationToken.None);

        first.Should().Be(second);
        first.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task ComputeRequestHash_IsKeyed_DifferentServerKeyDifferentHash()
    {
        // The fingerprint must depend on the server-side key: a plain
        // unkeyed hash of a body containing a 6-digit PIN would be an
        // offline brute-force oracle (ADR-0009 / review H10).
        var bytes = Encoding.UTF8.GetBytes("""{"pin":"123456"}""");
        var otherKey = NewOptions();
        otherKey.HashKey = "a-completely-different-server-key-9876543210";
        var otherService = CreateService(NewContext(), otherKey);

        var hash = await _sut.ComputeRequestHashAsync(new MemoryStream(bytes), CancellationToken.None);
        var otherHash = await otherService.ComputeRequestHashAsync(new MemoryStream(bytes), CancellationToken.None);

        otherHash.Should().NotBe(hash);
    }

    [Fact]
    public async Task ComputeRequestHash_DifferentBody_DifferentHash()
    {
        var hash1 = await _sut.ComputeRequestHashAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("""{"amount":50}""")), CancellationToken.None);
        var hash2 = await _sut.ComputeRequestHashAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("""{"amount":51}""")), CancellationToken.None);

        hash2.Should().NotBe(hash1);
    }

    #endregion

    #region Claim / decision state machine

    [Fact]
    public async Task TryAcquire_NewKey_ClaimsProcessingRecord()
    {
        var userId = Guid.NewGuid();
        var key = Guid.NewGuid();

        var result = await _sut.TryAcquireAsync(userId, Endpoint, key, HashA, CancellationToken.None);

        result.IsReplay.Should().BeFalse();
        result.Record.Status.Should().Be(IdempotencyStatus.Processing);

        var persisted = await NewContext().IdempotencyRecords.SingleAsync();
        persisted.UserId.Should().Be(userId);
        persisted.Key.Should().Be(key);
        persisted.Status.Should().Be(IdempotencyStatus.Processing);
        persisted.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(24), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task TryAcquire_LiveProcessingSameHash_ThrowsInFlight()
    {
        var (userId, key) = await SeedAsync(IdempotencyStatus.Processing, HashA, ageMinutes: 1);

        var act = () => _sut.TryAcquireAsync(userId, Endpoint, key, HashA, CancellationToken.None);

        (await act.Should().ThrowAsync<IdempotencyException>())
            .Which.ErrorCode.Should().Be(Shared.Constants.ErrorCodes.IdempotencyInFlight);
    }

    [Theory]
    [InlineData(IdempotencyStatus.Processing)]
    [InlineData(IdempotencyStatus.Executed)]
    [InlineData(IdempotencyStatus.Completed)]
    public async Task TryAcquire_AnyStatusDifferentHash_ThrowsKeyReuse(IdempotencyStatus status)
    {
        var (userId, key) = await SeedAsync(status, HashA, ageMinutes: 1);

        var act = () => _sut.TryAcquireAsync(userId, Endpoint, key, HashB, CancellationToken.None);

        (await act.Should().ThrowAsync<IdempotencyException>())
            .Which.ErrorCode.Should().Be(Shared.Constants.ErrorCodes.IdempotencyKeyReuse);
    }

    [Fact]
    public async Task TryAcquire_CompletedSameHash_Replays()
    {
        var (userId, key) = await SeedAsync(IdempotencyStatus.Completed, HashA, ageMinutes: 1,
            responseStatusCode: 201, responseBody: """{"data":"original"}""");

        var result = await _sut.TryAcquireAsync(userId, Endpoint, key, HashA, CancellationToken.None);

        result.IsReplay.Should().BeTrue();
        result.Record.ResponseStatusCode.Should().Be(201);
        result.Record.ResponseBody.Should().Be("""{"data":"original"}""");
    }

    [Fact]
    public async Task TryAcquire_FreshExecutedSameHash_ThrowsInFlight()
    {
        // Executed but young: the original request is between its business
        // commit and its response store — still "in flight"; a short retry
        // will hit the replay.
        var (userId, key) = await SeedAsync(IdempotencyStatus.Executed, HashA, ageMinutes: 1);

        var act = () => _sut.TryAcquireAsync(userId, Endpoint, key, HashA, CancellationToken.None);

        (await act.Should().ThrowAsync<IdempotencyException>())
            .Which.ErrorCode.Should().Be(Shared.Constants.ErrorCodes.IdempotencyInFlight);
    }

    [Fact]
    public async Task TryAcquire_StaleExecutedSameHash_ThrowsResultUnknown()
    {
        // Executed and old: the claimant died AFTER committing — the
        // response will never be stored. Never re-execute, never guess.
        var (userId, key) = await SeedAsync(IdempotencyStatus.Executed, HashA, ageMinutes: 11);

        var act = () => _sut.TryAcquireAsync(userId, Endpoint, key, HashA, CancellationToken.None);

        (await act.Should().ThrowAsync<IdempotencyException>())
            .Which.ErrorCode.Should().Be(Shared.Constants.ErrorCodes.IdempotencyResultUnknown);
    }

    [Fact]
    public async Task TryAcquire_StaleProcessing_TakesOverWithFreshClaim()
    {
        var (userId, key) = await SeedAsync(IdempotencyStatus.Processing, HashA, ageMinutes: 11);
        var oldClaimId = (await NewContext().IdempotencyRecords.SingleAsync()).ClaimId;

        var result = await _sut.TryAcquireAsync(userId, Endpoint, key, HashA, CancellationToken.None);

        result.IsReplay.Should().BeFalse();
        result.Record.ClaimId.Should().NotBe(oldClaimId, "a takeover must re-fence the record");

        var persisted = await NewContext().IdempotencyRecords.SingleAsync();
        persisted.Status.Should().Be(IdempotencyStatus.Processing);
        persisted.ClaimId.Should().Be(result.Record.ClaimId);
    }

    [Fact]
    public async Task TryAcquire_ExpiredCompleted_ReclaimsFresh()
    {
        var (userId, key) = await SeedAsync(IdempotencyStatus.Completed, HashA, ageMinutes: 1, expired: true);

        var result = await _sut.TryAcquireAsync(userId, Endpoint, key, HashA, CancellationToken.None);

        result.IsReplay.Should().BeFalse("expired records are treated as absent");
        var persisted = await NewContext().IdempotencyRecords.SingleAsync();
        persisted.Status.Should().Be(IdempotencyStatus.Processing);
    }

    [Fact]
    public async Task TryAcquire_SameKeyDifferentUser_BothClaim()
    {
        var key = Guid.NewGuid();

        var first = await _sut.TryAcquireAsync(Guid.NewGuid(), Endpoint, key, HashA, CancellationToken.None);
        var second = await _sut.TryAcquireAsync(Guid.NewGuid(), Endpoint, key, HashA, CancellationToken.None);

        first.IsReplay.Should().BeFalse();
        second.IsReplay.Should().BeFalse("keys are scoped per user");
    }

    [Fact]
    public async Task TryAcquire_SameKeyDifferentEndpoint_BothClaim()
    {
        var userId = Guid.NewGuid();
        var key = Guid.NewGuid();

        var first = await _sut.TryAcquireAsync(userId, "POST api/transfers", key, HashA, CancellationToken.None);
        var second = await _sut.TryAcquireAsync(userId, "POST api/transactions/deposit", key, HashA, CancellationToken.None);

        first.IsReplay.Should().BeFalse();
        second.IsReplay.Should().BeFalse("keys are scoped per logical endpoint");
    }

    #endregion

    #region Executed flip & fencing

    [Fact]
    public async Task MarkExecutedPending_DoesNotPersistUntilBusinessSaveChanges()
    {
        var result = await _sut.TryAcquireAsync(Guid.NewGuid(), Endpoint, Guid.NewGuid(), HashA, CancellationToken.None);

        _sut.MarkExecutedPending(result.Record);

        // In-memory flip only: the DATABASE must still say Processing until
        // the business operation commits (that is the whole point — the flip
        // rides the business SaveChanges atomically).
        (await NewContext().IdempotencyRecords.SingleAsync())
            .Status.Should().Be(IdempotencyStatus.Processing);

        await _context.SaveChangesAsync(); // simulates the business commit

        (await NewContext().IdempotencyRecords.SingleAsync())
            .Status.Should().Be(IdempotencyStatus.Executed);
    }

    [Fact]
    public async Task MarkExecutedPending_RotatesClaimId()
    {
        var result = await _sut.TryAcquireAsync(Guid.NewGuid(), Endpoint, Guid.NewGuid(), HashA, CancellationToken.None);
        var claimTimeClaimId = result.Record.ClaimId;

        _sut.MarkExecutedPending(result.Record);

        result.Record.ClaimId.Should().NotBe(claimTimeClaimId,
            "rotation is the fence against replayed commits and stale claimants");
    }

    [Fact]
    public async Task BusinessCommit_AfterTakeover_AbortsInsteadOfDoubleExecuting()
    {
        // The H1/H11 fencing proof: claimant A stalls; B takes over the stale
        // claim; A's business commit (carrying the Executed flip) must FAIL —
        // otherwise both A and B would move money.
        var userId = Guid.NewGuid();
        var key = Guid.NewGuid();

        var claimA = await _sut.TryAcquireAsync(userId, Endpoint, key, HashA, CancellationToken.None);
        _sut.MarkExecutedPending(claimA.Record);

        // B (separate request/context) takes over: rotates the row's ClaimId
        using (var contextB = NewContext())
        {
            var row = await contextB.IdempotencyRecords.SingleAsync();
            row.ClaimId = Guid.NewGuid();
            await contextB.SaveChangesAsync();
        }

        // A's "business commit" now flushes its pending flip with a stale fence
        var act = () => _context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "the ClaimId concurrency token must fence out the stale claimant");
    }

    #endregion

    #region Complete & release

    [Fact]
    public async Task Complete_StoresResponseAndStatus()
    {
        var result = await _sut.TryAcquireAsync(Guid.NewGuid(), Endpoint, Guid.NewGuid(), HashA, CancellationToken.None);
        _sut.MarkExecutedPending(result.Record);
        await _context.SaveChangesAsync(); // business commit

        await _sut.CompleteAsync(result.Record, 201, "application/json", """{"ok":true}""", CancellationToken.None);

        var persisted = await NewContext().IdempotencyRecords.SingleAsync();
        persisted.Status.Should().Be(IdempotencyStatus.Completed);
        persisted.ResponseStatusCode.Should().Be(201);
        persisted.ResponseContentType.Should().Be("application/json");
        persisted.ResponseBody.Should().Be("""{"ok":true}""");
    }

    [Fact]
    public async Task Release_ProcessingRecord_DeletesIt()
    {
        var userId = Guid.NewGuid();
        var key = Guid.NewGuid();
        var result = await _sut.TryAcquireAsync(userId, Endpoint, key, HashA, CancellationToken.None);
        var claimTimeClaimId = result.Record.ClaimId;
        _sut.MarkExecutedPending(result.Record); // pending only, never committed

        await _sut.ReleaseIfNotExecutedAsync(userId, Endpoint, key, claimTimeClaimId);

        (await NewContext().IdempotencyRecords.AnyAsync()).Should().BeFalse(
            "nothing committed -> the key must become reusable");
    }

    [Fact]
    public async Task Release_ExecutedRecord_KeepsIt()
    {
        var userId = Guid.NewGuid();
        var key = Guid.NewGuid();
        var result = await _sut.TryAcquireAsync(userId, Endpoint, key, HashA, CancellationToken.None);
        var claimTimeClaimId = result.Record.ClaimId;
        _sut.MarkExecutedPending(result.Record);
        await _context.SaveChangesAsync(); // the business COMMITTED

        // ... then something failed downstream (post-commit exception)
        await _sut.ReleaseIfNotExecutedAsync(userId, Endpoint, key, claimTimeClaimId);

        var persisted = await NewContext().IdempotencyRecords.SingleAsync();
        persisted.Status.Should().Be(IdempotencyStatus.Executed,
            "a committed operation must never be released for re-execution");
    }

    [Fact]
    public async Task Release_RecordOwnedBySomeoneElse_LeavesItAlone()
    {
        var userId = Guid.NewGuid();
        var key = Guid.NewGuid();
        var result = await _sut.TryAcquireAsync(userId, Endpoint, key, HashA, CancellationToken.None);
        var staleClaimId = Guid.NewGuid(); // NOT the claim-time value

        await _sut.ReleaseIfNotExecutedAsync(userId, Endpoint, key, staleClaimId);

        (await NewContext().IdempotencyRecords.AnyAsync()).Should().BeTrue(
            "releases are fenced: only the claim owner may delete the row");
        _ = result;
    }

    #endregion

    #region Helpers

    private async Task<(Guid UserId, Guid Key)> SeedAsync(
        IdempotencyStatus status, string requestHash, int ageMinutes,
        bool expired = false, int? responseStatusCode = null, string? responseBody = null)
    {
        var userId = Guid.NewGuid();
        var key = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = NewContext();
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            UserId = userId,
            Endpoint = Endpoint,
            Key = key,
            ClaimId = Guid.NewGuid(),
            RequestHash = requestHash,
            Status = status,
            ResponseStatusCode = responseStatusCode,
            ResponseContentType = responseBody is null ? null : "application/json",
            ResponseBody = responseBody,
            CreatedAt = now.AddMinutes(-ageMinutes),
            ExpiresAt = expired ? now.AddMinutes(-1) : now.AddHours(24)
        });
        await db.SaveChangesAsync();
        return (userId, key);
    }

    #endregion
}
