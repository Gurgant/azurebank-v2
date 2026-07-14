using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// Thrown by the idempotency middleware for protocol violations on
/// idempotent (monetary) endpoints. Converted to RFC 9457 ProblemDetails
/// with errorCode + traceId by AppExceptionHandler.
///
/// Status codes used (ADR-0009):
/// - 400: Idempotency-Key header missing or not a valid UUID
/// - 409: same key currently in flight, or executed with the response lost
/// - 422: same key reused with a different request payload
/// </summary>
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

    public static IdempotencyException ResultUnknown() => new(
        "A request with this idempotency key was executed, but its response was not recorded. " +
        "Verify the operation outcome via GET /api/transactions before retrying with a new key.",
        ErrorCodes.IdempotencyResultUnknown,
        409);

    public static IdempotencyException PayloadTooLarge() => new(
        "The request body exceeds the 32 KB limit for this endpoint.",
        ErrorCodes.IdempotencyPayloadTooLarge,
        413);
}
