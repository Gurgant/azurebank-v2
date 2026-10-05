using AzureBank.Api.Attributes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// Declares the 413 response on [RefuseOversizedBody] endpoints: bodies over 32 KB are refused with
/// PAYLOAD_TOO_LARGE.
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
