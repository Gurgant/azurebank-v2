using AzureBank.Shared.Entities;

namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Result of attempting to acquire an idempotency key (ADR-0009).
/// Either the key was acquired (Record is a live claim tracked by the
/// request-scoped DbContext) or a stored response must be replayed.
/// Conflict outcomes (409/422) are signaled via IdempotencyException.
/// </summary>
public sealed class IdempotencyAcquisition
{
    public required IdempotencyRecord Record { get; init; }
    public required bool IsReplay { get; init; }
}

/// <summary>
/// Persistence and decision logic for idempotent (monetary) endpoints.
/// Scoped: shares the request-scoped DbContext with the business services,
/// so the Executed flip can ride the business commit atomically.
/// </summary>
public interface IIdempotencyService
{
    /// <summary>
    /// Computes the keyed fingerprint (HMAC-SHA256, lowercase hex) of the raw
    /// request body stream. The caller must rewind the stream afterwards.
    /// </summary>
    Task<string> ComputeRequestHashAsync(Stream body, CancellationToken cancellationToken);

    /// <summary>
    /// Claims (userId, endpoint, key) or resolves the existing record:
    /// replay (Completed + same hash), 409 in-flight, 409 result-unknown,
    /// 422 key-reuse — conflicts thrown as IdempotencyException.
    /// Expired rows and stale Processing rows are taken over (fenced by ClaimId).
    /// </summary>
    Task<IdempotencyAcquisition> TryAcquireAsync(
        Guid userId, string endpoint, Guid key, string requestHash,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks the acquired record as pending-Executed on the request-scoped
    /// DbContext WITHOUT saving: the update rides the first business
    /// SaveChanges atomically. Also rotates ClaimId so any replayed commit or
    /// concurrent claimant fails its own UPDATE instead of executing twice.
    /// </summary>
    void MarkExecutedPending(IdempotencyRecord record);

    /// <summary>
    /// Stores the successful response and flips the record to Completed.
    /// </summary>
    Task CompleteAsync(
        IdempotencyRecord record, int statusCode, string? contentType, string body,
        CancellationToken cancellationToken);

    /// <summary>
    /// Error path: releases the claim ONLY if the database truth is still
    /// Processing (provably nothing committed). An Executed record is kept so
    /// retries get 409 IDEMPOTENCY_RESULT_UNKNOWN instead of re-executing.
    /// Uses its own DbContext scope: the request-scoped context may hold
    /// failed business changes that must never be re-flushed.
    /// </summary>
    Task ReleaseIfNotExecutedAsync(
        Guid userId, string endpoint, Guid key, Guid claimTimeClaimId);
}
