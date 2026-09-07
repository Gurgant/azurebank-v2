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
/// <para>
/// On a [RequireIdempotency] endpoint the 422 is written EARLIER, by
/// IdempotencyOperationTransformer (operation transformers run before document transformers, and
/// Add422Response below returns when a 422 already exists). Until ADR-0050 the three money entries
/// here were therefore dead text: the document carried the idempotency transformer's generic blurb
/// and never this table's. That transformer now reads this table through
/// <see cref="TryGetDescription"/> and composes the per-endpoint rule with its own
/// IDEMPOTENCY_KEY_REUSE clause, so ONE table names the rules.
/// </para>
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
            "Business Rule Violation - The request violates domain constraints (e.g., recipient not found, self transfer, insufficient funds) or the day's external transfer limit (errorCode DAILY_LIMIT_EXCEEDED, checked before the balance).",
        ["POST /api/transactions/withdraw"] =
            "Business Rule Violation - The request violates domain constraints (e.g., insufficient funds).",
        // ADR-0050: the external mint answers 422 four ways, named here in wire order — the payee
        // resolution's two codes (TransferService.ResolveExternalPayeeAsync, before the daily check),
        // the day's ceiling BEFORE the PIN is consulted (the ADR-0049 D4 rung), and the PIN
        // verifier's PIN_REQUIRED. Declared here and NOT by an attribute on
        // TransferController.AuthoriseTransfer, which was removed for the reason on the deletion
        // mint below: it outranked this entry and published the bare reason phrase. The internal
        // mint has no entry on purpose — it checks no daily limit — and still carries its
        // attribute, so its 422 stays the bare phrase: an existing drift named, not fixed here.
        ["POST /api/transfers/authorizations"] =
            "Business Rule Violation - the payee cannot be paid (errorCode SELF_TRANSFER_NOT_ALLOWED or RECIPIENT_NO_ACCOUNT), the day's external transfer limit would be exceeded (errorCode DAILY_LIMIT_EXCEEDED, checked before the PIN is consulted), or no PIN is enrolled (errorCode PIN_REQUIRED).",
        ["DELETE /api/accounts/{id}"] =
            "Business Rule Violation - The request violates domain constraints (e.g., primary account, non-zero balance).",
        // ADR-0049: the mint runs the two closure guards before the PIN is consulted, and the PIN
        // verifier adds a third code. Declared here and NOT by an attribute on the action, for the
        // reason on AccountController.DeleteAccount: an attribute would outrank this entry and
        // publish the bare reason phrase, which is what the document carried until 2026-09-06.
        ["POST /api/accounts/{id}/deletion-authorizations"] =
            "Business Rule Violation - the account cannot be closed (errorCode NON_ZERO_BALANCE or PRIMARY_ACCOUNT_DELETE, checked before the PIN is consulted), or no PIN is enrolled (errorCode PIN_REQUIRED).",
        ["GET /api/transactions/summary"] =
            "Business Rule Violation - The resolved date window is invalid, e.g. a lone future FromDate against the defaulted ToDate (errorCode: INVALID_DATE_RANGE)."
    };

    /// <summary>
    /// The per-endpoint 422 prose for <c>"METHOD /path"</c>, for the operation transformer that
    /// writes the 422 on [RequireIdempotency] endpoints before this one runs.
    /// </summary>
    internal static bool TryGetDescription(string operationKey, out string description)
        => BusinessRuleEndpoints.TryGetValue(operationKey, out description!);

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
