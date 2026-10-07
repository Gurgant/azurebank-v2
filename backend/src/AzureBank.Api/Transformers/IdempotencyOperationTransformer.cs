using System.Text.Json.Nodes;
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
/// - Documents the idempotency 409 (in flight / result unknown) and 422
///   (business rule violation / key reuse) ProblemDetails responses
/// - Declares <c>applied</c> on that 409, as true only: the member the result-unknown
///   answer carries when the API read the key's record from the database as committed
///
/// Note: runs BEFORE document transformers; the 422 added here (with the
/// full ProblemDetails schema) also covers the business-rule 422 that
/// BusinessRulesDocumentTransformer would otherwise add to these endpoints
/// (it skips operations that already document 422). Since ADR-0050 the
/// description is COMPOSED from that transformer's per-endpoint entry when one
/// exists, so the rules an endpoint can refuse on are named by one table and
/// not lost to this one's generic blurb — which is what happened to the three
/// money entries until then.
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
                "Missing => 400 IDEMPOTENCY_KEY_MISSING; malformed => 400 IDEMPOTENCY_KEY_INVALID.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid"
            }
        });

        operation.Responses ??= new OpenApiResponses();

        // Until 2026-10-01 the second half read "or it executed but its response was not recorded
        // (IDEMPOTENCY_RESULT_UNKNOWN: verify via GET /api/transactions)". Two things in it were
        // wrong for a client. The code is also answered when the key's record is gone and nothing
        // is known to have executed; and "verify" was the only advice even when the API had read
        // the record as committed, which left a second payment under a new key as the next step.
        // The answer now says which of the two it is, through applied, and so does this text.
        operation.Responses["409"] = new OpenApiResponse
        {
            Description =
                "Conflict - a request with this idempotency key is currently in flight " +
                "(IDEMPOTENCY_IN_FLIGHT), or its result cannot be returned " +
                "(IDEMPOTENCY_RESULT_UNKNOWN). On IDEMPOTENCY_RESULT_UNKNOWN, applied: true means " +
                "the operation was committed: do not send it again with a new key, look for it " +
                "with GET /api/transactions. Without applied the outcome is not known: verify via " +
                "GET /api/transactions before sending it again with a new key.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = CreateProblemDetailsSchema(statusCode: 409, withApplied: true)
                }
            }
        };
        // "POST /api/transfers": the key BusinessRulesDocumentTransformer's table uses. When the
        // endpoint has an entry there, its rules lead and the idempotency clause follows. The
        // fallback is for an idempotent endpoint with no business rule of its own — today only
        // POST /api/transactions/deposit, whose service throws no BusinessRuleException at all
        // (ownership 404/403, then the ledger write) — so it names the one refusal such an
        // endpoint can make and nothing else. Until ADR-0050's review it also said
        // "(e.g. INSUFFICIENT_FUNDS)", a refusal a deposit cannot answer; PublishedDailyLimitTests
        // pins the deposit's prose so the over-claim cannot come back.
        var operationKey =
            $"{context.Description.HttpMethod?.ToUpperInvariant()} /{context.Description.RelativePath}";
        var description422 = BusinessRulesDocumentTransformer.TryGetDescription(operationKey, out var rule)
            ? rule + " Also refused when this idempotency key was already used with a different " +
              "payload (IDEMPOTENCY_KEY_REUSE)."
            : "Unprocessable Entity - this idempotency key was already used with a different " +
              "payload (IDEMPOTENCY_KEY_REUSE).";

        operation.Responses["422"] = new OpenApiResponse
        {
            Description = description422,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = CreateProblemDetailsSchema(statusCode: 422)
                }
            }
        };
        operation.Responses["413"] = new OpenApiResponse
        {
            Description =
                "Payload Too Large - the request body exceeds the 32 KB limit for these " +
                "idempotent monetary endpoints (IDEMPOTENCY_PAYLOAD_TOO_LARGE); rejected " +
                "before any hashing or claim.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = CreateProblemDetailsSchema(statusCode: 413)
                }
            }
        };

        // Document the Idempotency-Replayed response header on the success
        // responses so the spec is 1:1 with the middleware: a replay carries
        // 'Idempotency-Replayed: true' (ADR-0009).
        foreach (var kvp in operation.Responses)
        {
            if (kvp.Key.StartsWith("2", StringComparison.Ordinal)
                && kvp.Value is OpenApiResponse successResponse)
            {
                successResponse.Headers ??= new Dictionary<string, IOpenApiHeader>();
                successResponse.Headers[IdempotencyConstants.ReplayedHeaderName] = new OpenApiHeader
                {
                    Description =
                        "Set to 'true' when this response is a byte-identical replay of a " +
                        "previously executed idempotent request (absent on first execution).",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                };
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// RFC 9457 ProblemDetails schema with the errorCode + traceId extensions
    /// (same shape as BusinessRulesDocumentTransformer documents).
    /// </summary>
    /// <param name="statusCode">The status the schema is declared for.</param>
    /// <param name="withApplied">
    /// Adds <c>applied</c>. Only the 409 passes it: of the answers these three statuses carry,
    /// <c>IdempotencyException.ResultUnknownApplied</c> is the one that sends the member, and a
    /// 422 or a 413 declaring it would publish a member they never send.
    /// </param>
    private static OpenApiSchema CreateProblemDetailsSchema(int statusCode, bool withApplied = false)
    {
        var schema = new OpenApiSchema
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
                    Description = $"The HTTP status code ({statusCode})"
                },
                ["detail"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "A human-readable explanation of the failure"
                },
                ["errorCode"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Machine-readable error code (e.g., 'INSUFFICIENT_FUNDS', 'IDEMPOTENCY_KEY_REUSE')"
                },
                ["traceId"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Request trace identifier for debugging"
                }
            }
        };

        if (withApplied)
        {
            schema.Properties["applied"] = new OpenApiSchema
            {
                // true is the only value a 409 carries, so it is the only one published: the
                // member is absent, never false, when the commit is not proven. A plain boolean
                // would let a generated client read a false here as "nothing moved", which no
                // 409 says. The outage 503 is the mirror: its applied is published as false only
                // (ServiceUnavailableResponseTransformer).
                Type = JsonSchemaType.Boolean,
                Enum = [JsonValue.Create(true)],
                Description =
                    "Present, and true, only on IDEMPOTENCY_RESULT_UNKNOWN and only when the API " +
                    "read this key's record from the database as executed: the operation was " +
                    "committed. Absent on IDEMPOTENCY_IN_FLIGHT, and whenever the outcome is not " +
                    "known."
            };
        }

        return schema;
    }
}
