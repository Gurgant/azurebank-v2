namespace AzureBank.Api.Attributes;

/// <summary>
/// Marks an endpoint as requiring an Idempotency-Key header (ADR-0009).
///
/// The IdempotencyMiddleware reads this marker from endpoint metadata and
/// enforces single execution per (user, endpoint, key); the
/// IdempotencyOperationTransformer documents the header and the 409/422
/// responses in the OpenAPI spec.
///
/// Apply to mutating monetary endpoints (deposit, withdraw, transfers).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireIdempotencyAttribute : Attribute
{
}
