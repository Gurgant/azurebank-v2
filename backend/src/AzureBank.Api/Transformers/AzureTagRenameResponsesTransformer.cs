using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI operation transformer for PATCH /api/users/me/azuretag (ADR-0015):
/// documents the 409 AZURE_TAG_TAKEN conflict (with the API's real error envelope)
/// and the 429 from the BFF's per-user handle-rename rate limit (ADR-0014).
///
/// These sections used to exist only as hand-enrichments in the committed spec, which
/// a regeneration silently DROPPED (proven during the reveal-endpoint PR). Encoding
/// them here makes `curl /openapi/v1.json` reproduce the committed contract.
/// </summary>
public sealed class AzureTagRenameResponsesTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var isRename =
            string.Equals(context.Description.HttpMethod, "PATCH", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                context.Description.RelativePath,
                "api/users/me/azuretag",
                StringComparison.OrdinalIgnoreCase);
        if (!isRename)
        {
            return Task.CompletedTask;
        }

        var responses = operation.Responses ??= new OpenApiResponses();

        responses["409"] = new OpenApiResponse
        {
            Description = "Conflict - the requested AzureTag is already taken (errorCode AZURE_TAG_TAKEN).",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = CreateConflictProblemSchema()
                }
            }
        };

        responses["429"] = new OpenApiResponse
        {
            Description = "Too Many Requests",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchemaReference("ProblemDetails")
                }
            }
        };

        return Task.CompletedTask;
    }

    /// <summary>
    /// The API's real conflict envelope: RFC 7807 ProblemDetails + errorCode + traceId,
    /// mirroring the inline-schema style of the validation transformer.
    /// </summary>
    private static OpenApiSchema CreateConflictProblemSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["type"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "A URI reference identifying the problem type"
                },
                ["title"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "A short, human-readable summary"
                },
                ["status"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer,
                    Description = "The HTTP status code (409)"
                },
                ["detail"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "A human-readable explanation of the failure"
                },
                ["errorCode"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Machine-readable error code (AZURE_TAG_TAKEN)"
                },
                ["traceId"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Request trace identifier for debugging"
                }
            }
        };
    }
}
