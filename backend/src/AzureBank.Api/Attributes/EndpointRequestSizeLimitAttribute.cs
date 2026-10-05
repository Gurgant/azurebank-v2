using Microsoft.AspNetCore.Http.Metadata;

namespace AzureBank.Api.Attributes;

/// <summary>
/// Declares a body limit for routing to apply before authentication and idempotency (ADR-0009).
/// No MVC filter: idempotency has already read the body when MVC runs, so the server's limit is
/// read-only by then.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EndpointRequestSizeLimitAttribute(long maxRequestBodySize) : Attribute, IRequestSizeLimitMetadata
{
    public long? MaxRequestBodySize { get; } = maxRequestBodySize;
}
