namespace AzureBank.Shared.Constants;

/// <summary>
/// Wire-level constants for the idempotency mechanism (ADR-0009).
/// </summary>
public static class IdempotencyConstants
{
    /// <summary>
    /// Request header carrying the client-generated idempotency key (UUID).
    /// Required on all monetary mutation endpoints.
    /// </summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>
    /// Response header set to "true" when the response is a stored replay
    /// of a previously completed request (same user, endpoint, key, payload).
    /// </summary>
    public const string ReplayedHeaderName = "Idempotency-Replayed";
}
