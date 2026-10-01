using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// Thrown by the idempotency middleware for protocol violations on
/// idempotent (monetary) endpoints. Converted to RFC 9457 ProblemDetails
/// with errorCode + traceId by AppExceptionHandler.
///
/// Status codes used (ADR-0009):
/// - 400: Idempotency-Key header missing or not a valid UUID
/// - 409: same key currently in flight, or its result cannot be returned: applied, or not known
/// - 422: same key reused with a different request payload
/// </summary>
/// <remarks>
/// Until 2026-10-01 the second 409 read "executed with the response lost", in one sentence for
/// every path. It is two answers now: <see cref="ResultUnknownApplied"/>, where the key's record
/// was read from the database as committed, and <see cref="ResultUnknown"/>, where the record is
/// gone and nothing is proven.
/// </remarks>
public class IdempotencyException(string message, string errorCode, int statusCode)
    : AppException(message, errorCode, statusCode)
{
    public static IdempotencyException KeyMissing() => new(
        $"The '{IdempotencyConstants.HeaderName}' header is required on this endpoint.",
        ErrorCodes.IdempotencyKeyMissing,
        400);

    public static IdempotencyException KeyInvalid() => new(
        $"The '{IdempotencyConstants.HeaderName}' header must be a valid UUID.",
        ErrorCodes.IdempotencyKeyInvalid,
        400);

    public static IdempotencyException KeyReuse() => new(
        "This idempotency key was already used with a different request payload.",
        ErrorCodes.IdempotencyKeyReuse,
        422);

    public static IdempotencyException InFlight() => new(
        "A request with this idempotency key is already in flight. Retry shortly.",
        ErrorCodes.IdempotencyInFlight,
        409);

    /// <summary>
    /// The key's record was read from the database as committed just now, by the request that
    /// answers: the operation was applied, and this request cannot return its result. The one
    /// answer that carries <c>applied: true</c> (ADR-0009).
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a caller that READ the record as <c>Executed</c> or <c>Completed</c> from the database,
    /// never for one that only holds the tracked record: the middleware sets <c>Executed</c> on
    /// that in memory before anything is committed.
    /// </para>
    /// <para>
    /// The sentence is true on every path that reaches it. It does not say the response was lost,
    /// since a record read as <c>Completed</c> has a stored one; and it tells the client to look
    /// for the operation, not that the list has it: a movement on an account closed afterwards is
    /// not listed.
    /// </para>
    /// </remarks>
    public static IdempotencyException ResultUnknownApplied() => new(
        "The operation sent with this idempotency key was applied, but this request cannot return " +
        "its result. Do not send it again with a new key: look for it with GET /api/transactions.",
        ErrorCodes.IdempotencyResultUnknown,
        409)
    {
        Details = new Dictionary<string, object> { ["applied"] = true },
    };

    /// <summary>
    /// The key's record is no longer there, so nothing about the outcome is proven: the same code
    /// as <see cref="ResultUnknownApplied"/>, with no <c>applied</c> member at all.
    /// </summary>
    /// <remarks>
    /// Until 2026-10-01 this was the one answer of every path and said "was executed, but its
    /// response was not recorded ... before retrying with a new key": for a record read as
    /// committed that invited a second payment of a proven one, and for a record that is gone
    /// "was executed" was never proven.
    /// </remarks>
    public static IdempotencyException ResultUnknown() => new(
        "A request with this idempotency key may have been executed: its record is no longer there, " +
        "so the outcome is not known. Verify via GET /api/transactions before sending it again with " +
        "a new key.",
        ErrorCodes.IdempotencyResultUnknown,
        409);

    public static IdempotencyException PayloadTooLarge() => new(
        "The request body exceeds the 32 KB limit for this endpoint.",
        ErrorCodes.IdempotencyPayloadTooLarge,
        413);
}
