using AzureBank.Shared.Constants;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI Document Transformer that adds 422 Unprocessable Entity responses
/// to endpoints that have business rule validations.
///
/// Business rules are domain constraints that cannot be expressed in JSON Schema:
/// - Insufficient funds
/// - Cannot delete primary account
/// (Not the same account on both sides of an internal transfer: the validators refuse that as a
/// 400 before any service runs, measured 2026-09-11.)
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
        // The three money moves name their codes rather than examples, each one measured on the
        // real API on 2026-09-11. Two of the examples they replaced could not happen: the same
        // account on both sides is refused by the validator as a 400 before the service is
        // reached, and an unknown recipient is a 404 (ACCOUNT_NOT_FOUND), never this 422.
        ["POST /api/transfers/internal"] =
            "Business Rule Violation - the source account cannot cover the amount (errorCode INSUFFICIENT_FUNDS).",
        ["POST /api/transfers"] =
            "Business Rule Violation - the payee cannot be paid (errorCode SELF_TRANSFER_NOT_ALLOWED or RECIPIENT_NO_ACCOUNT), the day's external transfer limit would be exceeded (errorCode DAILY_LIMIT_EXCEEDED, checked before the balance), or the source account cannot cover the amount (errorCode INSUFFICIENT_FUNDS).",
        ["POST /api/transactions/withdraw"] =
            "Business Rule Violation - no PIN is enrolled (errorCode PIN_REQUIRED, checked before the balance), or the account cannot cover the amount (errorCode INSUFFICIENT_FUNDS).",
        // ADR-0050: the external mint answers 422 four ways, named here in wire order — the payee
        // resolution's two codes (TransferService.ResolveExternalPayeeAsync, before the daily check),
        // the day's ceiling BEFORE the PIN is consulted (the ADR-0049 D4 rung), and the PIN
        // verifier's PIN_REQUIRED. Declared here and NOT by an attribute on
        // TransferController.AuthoriseTransfer, which was removed for the reason on the deletion
        // mint below: it outranked this entry and published the bare reason phrase.
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
        // The internal mint published the bare reason phrase until 2026-09-11, through the same
        // outranking attribute. Its one 422 is the PIN verifier's: AuthoriseInternalTransferAsync
        // also throws SAME_ACCOUNT_TRANSFER, but the validator refuses that pair first, as a 400
        // (measured), so naming it here would promise a refusal the server never sends.
        ["POST /api/transfers/internal/authorizations"] =
            "Business Rule Violation - no PIN is enrolled (errorCode PIN_REQUIRED).",
        // ADR-0040: each PIN transition costs a proof, and a MISSING one is this 422 — both codes
        // measured 2026-09-11. Declared here, not by an attribute on AuthController.SetPin, which
        // published the bare reason phrase until then.
        ["POST /api/auth/pin"] =
            "Business Rule Violation - the proof this change needs is missing: the password when enrolling a PIN (errorCode PASSWORD_REQUIRED), or the current PIN when changing one (errorCode PIN_REQUIRED).",
        ["GET /api/transactions/summary"] =
            "Business Rule Violation - The resolved date window is invalid, e.g. a lone future FromDate against the defaulted ToDate (errorCode: INVALID_DATE_RANGE)."
    };

    /// <summary>
    /// The per-endpoint 422 prose for <c>"METHOD /path"</c>, for the operation transformer that
    /// writes the 422 on [RequireIdempotency] endpoints before this one runs.
    /// </summary>
    internal static bool TryGetDescription(string operationKey, out string description)
        => BusinessRuleEndpoints.TryGetValue(operationKey, out description!);

    /// <summary>
    /// The refusals whose 422 body carries numeric members, per operation.
    /// <c>DAILY_LIMIT_EXCEEDED</c> spreads <c>limit</c>, <c>used</c>, <c>requested</c> and
    /// <c>resetsAt</c> into the body (ADR-0050 D7); <c>INSUFFICIENT_FUNDS</c> spreads
    /// <c>available</c> and <c>requested</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EXACTLY THESE, and each pairing was measured on the real API. The day's ceiling is checked
    /// by the external mint (<c>TransferService.AuthoriseTransferAsync</c>) and the external
    /// transfer only — the pair <c>PublishedDailyLimitTests</c> pins from the other side. The
    /// balance is checked by the three money moves and by neither mint: on 2026-09-11 both mints
    /// answered 201 for more than the balance, and the transfer each authorised then answered
    /// <c>INSUFFICIENT_FUNDS</c>. A deposit checks neither. Publishing a member where no path can
    /// send it would be a contract wider than the code — the same failure the per-endpoint 422
    /// prose above exists to avoid.
    /// </para>
    /// <para>
    /// WHY THE MEMBERS ARE ADDED AFTER THE RESPONSE RATHER THAN INSIDE IT.
    /// The three money moves are <c>[RequireIdempotency]</c>, so their 422 is written EARLIER by
    /// <see cref="IdempotencyOperationTransformer"/> and <see cref="Add422Response"/> returns
    /// without touching it. A member set written only into this class's own schema factory would
    /// therefore have reached the mint alone and silently missed all three — the trap that made
    /// the three money entries' prose dead text before ADR-0050. This amends whichever 422 schema
    /// is there when document transformers run, which is after every operation transformer.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string[]> RefusalsWithMembers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["POST /api/transfers"] = [ErrorCodes.DailyLimitExceeded, ErrorCodes.InsufficientFunds],
            ["POST /api/transfers/authorizations"] = [ErrorCodes.DailyLimitExceeded],
            ["POST /api/transfers/internal"] = [ErrorCodes.InsufficientFunds],
            ["POST /api/transactions/withdraw"] = [ErrorCodes.InsufficientFunds],
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

                if (RefusalsWithMembers.TryGetValue(operationKey, out var codes))
                {
                    DeclareRefusalMembers(operation, codes);
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
    /// Declares the members this operation's refusals put in the body, on the 422 schema it
    /// already has — whoever wrote it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY DECLARE THEM AT ALL. ADR-0043's thesis is that the document declares the error body so
    /// a generated client can branch on it; numbers that arrive on the wire and appear nowhere in
    /// the contract cannot be branched on without hand-written types. The 422 schemas on these
    /// operations are already INLINE objects rather than a <c>$ref</c> to the ProblemDetails
    /// component, so adding members to them is this document's existing idiom and not a new
    /// practice, and it leaves the shared component alone.
    /// </para>
    /// <para>
    /// EACH MEMBER RIDES SOME CODES AND NOT OTHERS, so none of them is required: the same 422
    /// answers <c>SELF_TRANSFER_NOT_ALLOWED</c>, <c>RECIPIENT_NO_ACCOUNT</c>, <c>PIN_REQUIRED</c>
    /// and <c>IDEMPOTENCY_KEY_REUSE</c> with none of them, and each description names the codes
    /// that carry it rather than leaving a client to discover it.
    /// </para>
    /// <para>
    /// <c>requested</c> is the one member two codes share, so its description is built from the
    /// operation's codes. On <c>POST /api/transfers</c> it read "DAILY_LIMIT_EXCEEDED only" until
    /// 2026-09-11, while the <c>INSUFFICIENT_FUNDS</c> body there carried it too — measured on
    /// 2026-09-07 as <c>{"available": 300.0, "requested": 400}</c>, and again on 2026-09-11 as
    /// <c>"available":100.2500,"requested":500.5</c>.
    /// </para>
    /// </remarks>
    private static void DeclareRefusalMembers(OpenApiOperation operation, string[] codes)
    {
        if (operation.Responses is null
            || !operation.Responses.TryGetValue("422", out var response)
            || response.Content is not { } content
            || !content.TryGetValue("application/json", out var media)
            || media.Schema is not OpenApiSchema { Properties: not null } schema)
        {
            // Nothing to amend means nothing published the 422 this document transformer runs
            // after. Silent here on purpose: PublishedDailyLimitTests and
            // PublishedRefusalCodesTests fail on the committed document if that ever happens,
            // which is a louder place to find out than a throw during document generation.
            return;
        }

        var dailyLimit = codes.Contains(ErrorCodes.DailyLimitExceeded);
        var insufficientFunds = codes.Contains(ErrorCodes.InsufficientFunds);

        if (dailyLimit)
        {
            schema.Properties["limit"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Number,
                Description =
                    "DAILY_LIMIT_EXCEEDED only: the ceiling on the sum of this user's completed "
                    + "outgoing external transfers in the current UTC day."
            };
            schema.Properties["used"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Number,
                Description =
                    "DAILY_LIMIT_EXCEEDED only: how much of that ceiling the user's completed "
                    + "outgoing external transfers had already taken when this request was "
                    + "refused. Today's remaining headroom is limit - used; no member carries it, "
                    + "so there is one source of truth."
            };
        }

        if (insufficientFunds)
        {
            schema.Properties["available"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Number,
                Description =
                    "INSUFFICIENT_FUNDS only: the balance of the account the money would have "
                    + "left, when this request was refused. It is less than requested."
            };
        }

        schema.Properties["requested"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Number,
            Description = (dailyLimit, insufficientFunds) switch
            {
                (true, false) =>
                    "DAILY_LIMIT_EXCEEDED only: the amount this request asked to move. It was not "
                    + "moved: used + requested would have exceeded limit, and nothing was written.",
                (false, true) =>
                    "INSUFFICIENT_FUNDS only: the amount this request asked to move. It was not "
                    + "moved: it is more than available.",
                (true, true) =>
                    "DAILY_LIMIT_EXCEEDED and INSUFFICIENT_FUNDS: the amount this request asked to "
                    + "move, under either code. It was not moved: under DAILY_LIMIT_EXCEEDED, used "
                    + "+ requested would have exceeded limit; under INSUFFICIENT_FUNDS, it is more "
                    + "than available.",
                (false, false) => throw new InvalidOperationException(
                    "RefusalsWithMembers lists an operation with no code that carries a member."),
            }
        };

        if (dailyLimit)
        {
            schema.Properties["resetsAt"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "date-time",
                Description =
                    "DAILY_LIMIT_EXCEEDED only: the UTC instant the window reopens — the start of "
                    + "the next UTC day. Sent so a client need not know that the window is a "
                    + "calendar day."
            };
        }
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
