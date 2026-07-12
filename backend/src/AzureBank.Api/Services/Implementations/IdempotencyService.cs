using System.Security.Cryptography;
using System.Text;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// Idempotency persistence and decision logic (ADR-0009).
///
/// The composite PK (UserId, Endpoint, Key) is the distributed lock: the
/// claim INSERT can only succeed once. ClaimId (Guid concurrency token) is
/// the fencing/owner token: every UPDATE/DELETE is conditional on it, so a
/// stale claimant or a replayed commit aborts (DbUpdateConcurrencyException)
/// instead of executing twice.
/// </summary>
public class IdempotencyService : IIdempotencyService
{
    private const int MaxAcquireAttempts = 4;

    private readonly AzureBankDbContext _context;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IdempotencyOptions _options;
    private readonly ILogger<IdempotencyService> _logger;

    public IdempotencyService(
        AzureBankDbContext context,
        IServiceScopeFactory scopeFactory,
        IOptions<IdempotencyOptions> options,
        ILogger<IdempotencyService> logger)
    {
        _context = context;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Builds the logical endpoint identity: "{METHOD} {route pattern}".
    /// Derived from route metadata, never the raw request path: routing is
    /// case- and trailing-slash-tolerant, so raw paths would create distinct
    /// records for the same logical endpoint (and defeat single execution).
    /// </summary>
    public static string EndpointNameFor(string httpMethod, string routePattern) =>
        $"{httpMethod.ToUpperInvariant()} {routePattern}";

    /// <inheritdoc />
    public async Task<string> ComputeRequestHashAsync(Stream body, CancellationToken cancellationToken)
    {
        // Keyed digest (not plain SHA-256): the withdraw body contains a
        // 6-digit PIN, so an unkeyed hash of a mostly-known payload would be
        // an offline brute-force oracle for anyone with database read access.
        // The key lives in configuration (user-secrets/env), never in the DB.
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.HashKey));
        var hash = await hmac.ComputeHashAsync(body, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    /// <inheritdoc />
    public async Task<IdempotencyAcquisition> TryAcquireAsync(
        Guid userId, string endpoint, Guid key, string requestHash,
        CancellationToken cancellationToken)
    {
        // ClaimId of our own insert attempts. If EnableRetryOnFailure re-runs
        // a claim INSERT whose commit ack was lost, the retry hits our own
        // row's unique violation; recognizing our ClaimId on re-read means
        // "we own the lock" instead of a bogus 409.
        Guid? myClaimId = null;

        for (var attempt = 1; attempt <= MaxAcquireAttempts; attempt++)
        {
            var existing = await _context.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.UserId == userId && r.Endpoint == endpoint && r.Key == key,
                    cancellationToken);

            var now = DateTime.UtcNow;

            if (existing is null)
            {
                var record = new IdempotencyRecord
                {
                    UserId = userId,
                    Endpoint = endpoint,
                    Key = key,
                    ClaimId = Guid.NewGuid(),
                    RequestHash = requestHash,
                    Status = IdempotencyStatus.Processing,
                    CreatedAt = now,
                    ExpiresAt = now + _options.Ttl
                };
                myClaimId = record.ClaimId;

                _context.IdempotencyRecords.Add(record);
                try
                {
                    // The unique composite PK makes this INSERT the lock:
                    // exactly one concurrent claimant can succeed.
                    await _context.SaveChangesAsync(cancellationToken);
                    return new IdempotencyAcquisition { Record = record, IsReplay = false };
                }
                catch (Exception ex) when (IsDuplicateKey(ex))
                {
                    // Lost the race (or our own commit was retried by the
                    // resilient execution strategy). Detach the failed entity
                    // — a tracked instance with this PK would otherwise
                    // shadow the winner's row on re-read — and re-evaluate.
                    _context.Entry(record).State = EntityState.Detached;
                    continue;
                }
            }

            if (myClaimId is not null && existing.ClaimId == myClaimId)
            {
                // Our own INSERT actually committed (retried commit whose ack
                // was lost). Re-attach as the owned claim and proceed.
                _context.IdempotencyRecords.Attach(existing);
                return new IdempotencyAcquisition { Record = existing, IsReplay = false };
            }

            if (existing.ExpiresAt <= now)
            {
                // Idempotency window over: the row is dead weight. Remove it
                // (fenced) and claim fresh. Losing the fenced delete means a
                // concurrent claimant got there first — loop and re-read.
                await TryFencedDeleteAsync(existing, cancellationToken);
                continue;
            }

            switch (existing.Status)
            {
                case IdempotencyStatus.Completed:
                    if (existing.RequestHash != requestHash)
                    {
                        throw IdempotencyException.KeyReuse();
                    }
                    return new IdempotencyAcquisition { Record = existing, IsReplay = true };

                case IdempotencyStatus.Executed:
                    if (existing.RequestHash != requestHash)
                    {
                        throw IdempotencyException.KeyReuse();
                    }
                    // The operation committed but its response was never
                    // stored (crash/error after commit). Never re-execute,
                    // never guess a response.
                    throw IdempotencyException.ResultUnknown();

                case IdempotencyStatus.Processing:
                    if (existing.RequestHash != requestHash)
                    {
                        throw IdempotencyException.KeyReuse();
                    }
                    if (existing.CreatedAt + _options.ProcessingStaleAfter <= now)
                    {
                        // Stale Processing = the claimant died BEFORE
                        // committing anything (a committed operation would
                        // have flipped the row to Executed atomically), so a
                        // takeover is provably safe. The fenced delete makes
                        // it race-free: if the original claimant is actually
                        // alive, its own commit will fail on the rotated
                        // ClaimId and abort.
                        _logger.LogWarning(
                            "Taking over stale idempotency claim {Endpoint}/{Key} for user {UserId} (age {Age})",
                            existing.Endpoint, existing.Key, existing.UserId, now - existing.CreatedAt);
                        await TryFencedDeleteAsync(existing, cancellationToken);
                        continue;
                    }
                    throw IdempotencyException.InFlight();
            }
        }

        // Pathological contention (every attempt lost a race). 409 is the
        // honest, retryable answer.
        _logger.LogWarning(
            "Idempotency acquire gave up after {Attempts} attempts for {Endpoint}/{Key}",
            MaxAcquireAttempts, endpoint, key);
        throw IdempotencyException.InFlight();
    }

    /// <inheritdoc />
    public void MarkExecutedPending(IdempotencyRecord record)
    {
        // NOT saved here: the request-scoped DbContext flushes this update
        // inside the first business SaveChanges (for transfers: inside their
        // explicit transaction) — that is what makes Executed an atomic
        // marker of "the business operation committed".
        record.Status = IdempotencyStatus.Executed;

        // Rotate the fencing token. If a resilient execution strategy
        // re-runs an already-committed business commit (commit ack lost),
        // the re-run updates WHERE ClaimId = <old> → 0 rows →
        // DbUpdateConcurrencyException → abort. No in-request double execution.
        record.ClaimId = Guid.NewGuid();
    }

    /// <inheritdoc />
    public async Task CompleteAsync(
        IdempotencyRecord record, int statusCode, string? contentType, string body,
        CancellationToken cancellationToken)
    {
        record.Status = IdempotencyStatus.Completed;
        record.ResponseStatusCode = statusCode;
        record.ResponseContentType = contentType;
        record.ResponseBody = body;
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReleaseIfNotExecutedAsync(
        Guid userId, string endpoint, Guid key, Guid claimTimeClaimId)
    {
        // Deliberately a FRESH scope/context:
        // 1. The request-scoped context may hold failed business changes
        //    (Added transactions, modified balances) that a SaveChanges here
        //    must never re-flush.
        // 2. This runs on error paths where the request may already be
        //    aborted, hence CancellationToken.None.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var truth = await db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.UserId == userId && r.Endpoint == endpoint && r.Key == key,
                CancellationToken.None);

        if (truth is null)
        {
            return; // already taken over/cleaned
        }

        if (truth.Status != IdempotencyStatus.Processing)
        {
            // The business operation committed even though the request is
            // failing downstream (e.g. post-commit exception). Keep the
            // record: retries must get 409 IDEMPOTENCY_RESULT_UNKNOWN, never
            // a second execution.
            _logger.LogWarning(
                "Idempotency record {Endpoint}/{Key} for user {UserId} is {Status} on the error path; keeping it (no release)",
                endpoint, key, userId, truth.Status);
            return;
        }

        if (truth.ClaimId != claimTimeClaimId)
        {
            return; // someone else owns the row now
        }

        // Provably nothing committed → release the key so the client can
        // retry (same or fixed payload) without minting a new key.
        db.IdempotencyRecords.Remove(truth);
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Raced with a takeover/cleanup — the row is no longer ours.
        }
    }

    private async Task TryFencedDeleteAsync(IdempotencyRecord snapshot, CancellationToken cancellationToken)
    {
        // Conditional delete: EF includes the ClaimId concurrency token in
        // the WHERE clause, so deleting a row that changed since our read
        // affects 0 rows and throws instead of clobbering a live claim.
        _context.IdempotencyRecords.Attach(snapshot);
        _context.IdempotencyRecords.Remove(snapshot);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _context.Entry(snapshot).State = EntityState.Detached;
        }
    }

    private static bool IsDuplicateKey(Exception ex) =>
        // SQL Server surfaces unique violations as DbUpdateException
        // (SqlException 2627/2601 inside); the InMemory provider throws a
        // raw ArgumentException ("An item with the same key has already been
        // added") — verified empirically on EF Core 10.
        ex is DbUpdateException or ArgumentException;
}
