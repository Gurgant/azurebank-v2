using AzureBank.Api.Attributes;
using AzureBank.Shared.Constants;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI operation transformer for [RequireIdempotency] endpoints (ADR-0009).
///
/// Purpose:
/// - Documents the required Idempotency-Key header (uuid) so the spec stays
///   1:1 with live behavior and Schemathesis generates the header
/// - Documents the idempotency-specific 409 (in flight / result unknown) and
///   422 (key reuse with different payload) ProblemDetails responses
/// </summary>
public sealed class IdempotencyOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata is null || !metadata.OfType<RequireIdempotencyAttribute>().Any())
        {
            return Task.CompletedTask;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = IdempotencyConstants.HeaderName,
            In = ParameterLocation.Header,
            Required = true,
            Description =
                "Client-generated UUID that makes this monetary operation idempotent: " +
                "retries with the same key and payload replay the original response " +
                $"(header {IdempotencyConstants.ReplayedHeaderName}: true) instead of executing twice. " +
                "Missing → 400 IDEMPOTENCY_KEY_MISSING; malformed → 400 IDEMPOTENCY_KEY_INVALID.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid"
            }
        });

        operation.Responses ??= new OpenApiResponses();
        operation.Responses["409"] = new OpenApiResponse
        {
            Description =
                "Conflict - a request with this idempotency key is currently in flight " +
                "(IDEMPOTENCY_IN_FLIGHT), or it executed but its response was not recorded " +
                "(IDEMPOTENCY_RESULT_UNKNOWN: verify via GET /api/transactions)."
        };
        operation.Responses["422"] = new OpenApiResponse
        {
            Description =
                "Unprocessable Entity - business rule violation (e.g. INSUFFICIENT_FUNDS), " +
                "or this idempotency key was already used with a different payload " +
                "(IDEMPOTENCY_KEY_REUSE)."
        };

        return Task.CompletedTask;
    }
}
