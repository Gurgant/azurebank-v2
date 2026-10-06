using AzureBank.Api.Attributes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// Declares the 413 on the operations marked <c>[RefuseOversizedBody]</c>, the marker that answers
/// it: a body over the endpoint's 32,768-byte limit is refused with <c>PAYLOAD_TOO_LARGE</c>.
/// Filled in through <see cref="ProblemDetailsResponses"/>, so the body is the shared component.
/// The four money operations' 413 is not declared here: <c>IdempotencyOperationTransformer</c>
/// assigns it.
/// </summary>
public sealed class PayloadTooLargeResponseTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata?.OfType<RefuseOversizedBodyAttribute>().Any() != true)
        {
            return Task.CompletedTask;
        }

        operation.Responses ??= [];
        ProblemDetailsResponses.Declare(
            operation.Responses,
            "413",
            "Payload Too Large",
            "Payload Too Large - the request body exceeds the 32 KB limit for this endpoint (PAYLOAD_TOO_LARGE).");
        return Task.CompletedTask;
    }
}
