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
    /// Endpoints that have business rule validations returning 422, with the
    /// per-endpoint description of WHICH rule can fire (a shared generic blurb
    /// misleads clients — a date-window endpoint has no "insufficient funds").
    /// Format: "METHOD /path" → 422 description.
    /// </summary>
    private static readonly Dictionary<string, string> BusinessRuleEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POST /api/transfers/internal"] =
            "Business Rule Violation - The request violates domain constraints (e.g., same account transfer, insufficient funds).",
        ["POST /api/transfers"] =
            "Business Rule Violation - The request violates domain constraints (e.g., recipient not found, self transfer, insufficient funds).",
        ["POST /api/transactions/withdraw"] =
            "Business Rule Violation - The request violates domain constraints (e.g., insufficient funds).",
        ["DELETE /api/accounts/{id}"] =
            "Business Rule Violation - The request violates domain constraints (e.g., primary account, non-zero balance).",
        ["GET /api/transactions/summary"] =
            "Business Rule Violation - The resolved date window is invalid, e.g. a lone future FromDate against the defaulted ToDate (errorCode: INVALID_DATE_RANGE)."
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

                if (BusinessRuleEndpoints.TryGetValue(operationKey, out var description))
                {
                    Add422Response(operation, description);
                }
            }
        }

        return Task.CompletedTask;
    }

    private static void Add422Response(OpenApiOperation operation, string description)
    {
        operation.Responses ??= new OpenApiResponses();

        if (operation.Responses.ContainsKey("422"))
            return;

        operation.Responses["422"] = new OpenApiResponse
        {
            Description = description,
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
