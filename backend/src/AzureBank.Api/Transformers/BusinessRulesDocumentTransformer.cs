using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI Document Transformer that adds 422 Unprocessable Entity responses
/// to endpoints that have business rule validations.
///
/// Business rules are domain constraints that cannot be expressed in JSON Schema:
/// - Same account transfer (fromAccountId != toAccountId)
/// - Insufficient funds
/// - Cannot delete primary account
///
/// This transformer ensures these endpoints document 422 as a possible response,
/// which aligns with how BusinessRuleException now returns 422.
///
/// Reference: project-docs/30-business-rule-validation-implementation-plan.md
/// </summary>
public sealed class BusinessRulesDocumentTransformer : IOpenApiDocumentTransformer
{
    /// <summary>
    /// Endpoints that have business rule validations returning 422.
    /// Format: "METHOD /path"
    /// </summary>
    private static readonly HashSet<string> BusinessRuleEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST /api/transfers/internal",  // Same account, insufficient funds
        "POST /api/transfers",           // Recipient not found, self transfer, insufficient funds
        "POST /api/transactions/withdraw", // Insufficient funds
        "DELETE /api/accounts/{id}"      // Primary account, non-zero balance
    };

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (document.Paths == null)
            return Task.CompletedTask;

        foreach (var (path, pathItem) in document.Paths)
        {
            if (pathItem.Operations == null)
                continue;

            foreach (var (method, operation) in pathItem.Operations)
            {
                var operationKey = $"{method.ToString().ToUpperInvariant()} {path}";

                if (BusinessRuleEndpoints.Contains(operationKey))
                {
                    Add422Response(operation);
                }
            }
        }

        return Task.CompletedTask;
    }

    private static void Add422Response(OpenApiOperation operation)
    {
        operation.Responses ??= new OpenApiResponses();

        if (operation.Responses.ContainsKey("422"))
            return;

        operation.Responses["422"] = new OpenApiResponse
        {
            Description = "Business Rule Violation - The request violates domain constraints (e.g., same account transfer, insufficient funds).",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = CreateBusinessRuleProblemDetailsSchema()
                }
            }
        };
    }

    /// <summary>
    /// Creates a schema for RFC 7807 ProblemDetails for business rule violations.
    /// </summary>
    private static OpenApiSchema CreateBusinessRuleProblemDetailsSchema()
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
                    Description = "A short, human-readable summary (e.g., 'Business Rule Violation')"
                },
                ["status"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer,
                    Description = "The HTTP status code (422)"
                },
                ["detail"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "A human-readable explanation of the business rule violation"
                },
                ["errorCode"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Machine-readable error code (e.g., 'INSUFFICIENT_FUNDS')"
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
