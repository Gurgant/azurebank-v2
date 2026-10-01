using AzureBank.Api.Services;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// What a re-run attempt of an idempotent money operation decides from the record under its key
/// (<see cref="ConcurrencyRetry.PrepareIdempotentAttemptAsync"/>, ADR-0009): whether it may execute,
/// and, when it may not, what it is able to say about the first attempt.
/// </summary>
/// <remarks>
/// <para>
/// Two contexts over one store. The first is the request's: it tracks the claim and carries the
/// pending flip to <c>Executed</c> exactly as the middleware leaves it, in memory only. The second
/// is the rest of the world: an earlier attempt's commit, another request that took the claim over,
/// or a delete.
/// </para>
/// <para>
/// The InMemory provider shows the SHAPE of each answer, not the proof behind it: it has no
/// transactions, so "read as Executed" here is whatever the second context wrote. The same three
/// answers on SQL Server, over real commits and a real rollback, are in
/// <c>TransferTransientRetrySqlServerTests</c> and <c>WithdrawalStepUpSqlServerTests</c>.
/// </para>
/// </remarks>
public sealed class ConcurrencyRetryIdempotentAttemptTests : IDisposable
{
    private const string Endpoint = "POST api/transfers";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string AppliedSentence =
        "The operation sent with this idempotency key was applied, but this request cannot return "
        + "its result. Do not send it again with a new key: look for it with GET /api/transactions.";

    private const string NotKnownSentence =
        "A request with this idempotency key may have been executed: its record is no longer there, "
        + "so the outcome is not known. Verify via GET /api/transactions before sending it again with "
        + "a new key.";

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly InMemoryDatabaseRoot _root = new();
    private readonly AzureBankDbContext _request;

    public ConcurrencyRetryIdempotentAttemptTests()
    {
        _request = NewContext();
    }

    private AzureBankDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(_dbName, _root)
            .Options);

    public void Dispose() => _request.Dispose();

    [Theory]
    [InlineData(IdempotencyStatus.Executed)]
    [InlineData(IdempotencyStatus.Completed)]
    public async Task ARecordReloadedAsCommitted_RefusesToRunAgain_AndSaysTheOperationWasApplied(
        IdempotencyStatus stored)
    {
        // Executed: an earlier attempt of this request committed and its acknowledgement was lost.
        // Completed: another request with the same key and bytes took a stale claim over, committed
        // and stored its answer. Either way a commit under this key landed.
        var record = await ClaimAsync();
        await ElsewhereAsync(async db =>
        {
            var row = await db.IdempotencyRecords.SingleAsync();
            row.Status = stored;
            row.ClaimId = Guid.NewGuid();
            if (stored == IdempotencyStatus.Completed)
            {
                row.ResponseStatusCode = 201;
                row.ResponseContentType = "application/json";
                row.ResponseBody = """{"data":null,"message":"stored by the request that took over"}""";
            }

            await db.SaveChangesAsync();
        });

        var act = () => ConcurrencyRetry.PrepareIdempotentAttemptAsync(_request, [], CancellationToken.None);

        var refusal = (await act.Should().ThrowAsync<IdempotencyException>()).Which;
        refusal.ErrorCode.Should().Be(ErrorCodes.IdempotencyResultUnknown);
        refusal.StatusCode.Should().Be(409);
        _request.Entry(record).State.Should().NotBe(EntityState.Detached, "the row is still there");

        refusal.Details.Should().NotBeNull(
            "the record under the key was just read from the store as {0}: a commit landed, and the answer must say so",
            stored);
        refusal.Details!.Keys.Should().Equal("applied");
        refusal.Details["applied"].Should().BeOfType<bool>().Which.Should().BeTrue();
        refusal.Message.Should().Be(AppliedSentence);
    }

    [Fact]
    public async Task ARecordThatIsGoneOnReload_RefusesToRunAgain_AndDoesNotSayTheOperationWasApplied()
    {
        // The trap this holds: after a reload that finds no row, the tracked entity still says
        // Executed, because that is this request's own pending flip. A split that looked at the
        // status before it looked at the entry's state would answer "applied" for a row that is gone.
        var record = await ClaimAsync();
        await ElsewhereAsync(async db =>
        {
            db.IdempotencyRecords.Remove(await db.IdempotencyRecords.SingleAsync());
            await db.SaveChangesAsync();
        });

        var act = () => ConcurrencyRetry.PrepareIdempotentAttemptAsync(_request, [], CancellationToken.None);

        var refusal = (await act.Should().ThrowAsync<IdempotencyException>()).Which;
        refusal.ErrorCode.Should().Be(ErrorCodes.IdempotencyResultUnknown);
        refusal.StatusCode.Should().Be(409);

        _request.Entry(record).State.Should().Be(
            EntityState.Detached, "the reload found no row; this is the case under test");
        record.Status.Should().Be(
            IdempotencyStatus.Executed,
            "the tracked record still carries the request's own pending flip, which is why it proves nothing");

        refusal.Details.Should().BeNull("nothing was read from the store, so nothing is proven: no applied member at all");
        refusal.Message.Should().Be(NotKnownSentence);
    }

    [Fact]
    public async Task ARecordStillProcessingOnReload_IsReArmed_AndTheAttemptMayRun()
    {
        var record = await ClaimAsync();
        var pendingClaimId = record.ClaimId;
        var storedClaimId = await ElsewhereAsync(async db => (await db.IdempotencyRecords.SingleAsync()).ClaimId);

        await ConcurrencyRetry.PrepareIdempotentAttemptAsync(_request, [], CancellationToken.None);

        record.Status.Should().Be(
            IdempotencyStatus.Executed, "the flip is pending again, to ride this attempt's commit");
        record.ClaimId.Should().NotBe(storedClaimId, "the fence is rotated for this attempt")
            .And.NotBe(pendingClaimId, "and not with the failed attempt's value");
        _request.Entry(record).State.Should().Be(EntityState.Modified);

        var stored = await ElsewhereAsync(db => db.IdempotencyRecords.AsNoTracking().SingleAsync());
        stored.Status.Should().Be(IdempotencyStatus.Processing, "nothing is written until the business commit");
        stored.ClaimId.Should().Be(storedClaimId);
    }

    /// <summary>
    /// The request's claim as the middleware hands it to the action: inserted <c>Processing</c>,
    /// then flipped to <c>Executed</c> with a rotated <c>ClaimId</c> in memory only.
    /// </summary>
    private async Task<IdempotencyRecord> ClaimAsync()
    {
        var now = DateTime.UtcNow;
        var record = new IdempotencyRecord
        {
            UserId = Guid.NewGuid(),
            Endpoint = Endpoint,
            Key = Guid.NewGuid(),
            ClaimId = Guid.NewGuid(),
            RequestHash = Hash,
            Status = IdempotencyStatus.Processing,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24),
        };
        _request.IdempotencyRecords.Add(record);
        await _request.SaveChangesAsync();

        record.Status = IdempotencyStatus.Executed;
        record.ClaimId = Guid.NewGuid();
        return record;
    }

    private async Task ElsewhereAsync(Func<AzureBankDbContext, Task> work)
    {
        await using var db = NewContext();
        await work(db);
    }

    private async Task<T> ElsewhereAsync<T>(Func<AzureBankDbContext, Task<T>> work)
    {
        await using var db = NewContext();
        return await work(db);
    }
}
