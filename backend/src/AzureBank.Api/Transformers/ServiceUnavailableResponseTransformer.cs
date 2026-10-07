using System.Text.Json.Nodes;
using AzureBank.Api.Attributes;
using AzureBank.Api.Middleware;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// Declares the outage 503 on every operation (ADR-0058): any request can meet a database that
/// cannot be reached or, outside the three exempt token endpoints, its own deadline, and both answer
/// 503 <c>SERVICE_UNAVAILABLE</c>. On the four keyed money operations the body is the
/// <c>MoneyServiceUnavailable</c> component, which adds <c>applied</c>.
/// </summary>
/// <remarks>
/// <para>
/// Before, one operation declared a 503 — revoke, whose own refusal predates the outage answer — so
/// a client generated from the document had no type for the answer the other 29 now send. Declared
/// through <see cref="ProblemDetailsResponses"/>, so revoke's declaration is filled in, never
/// replaced (ADR-0043).
/// </para>
/// <para>
/// <c>applied</c> IS DECLARED ON THE MONEY 503s ONLY, through its own component, not on the shared
/// <c>ProblemDetails</c>. Only a <see cref="RequireIdempotencyAttribute"/> request holds a claim
/// whose fate the API knows, so only those four can ever send it
/// (<c>ServiceUnavailableExceptionHandler.NothingApplied</c>); on the shared component it would be
/// published for 26 operations that never send it, the over-claim
/// <c>PublishedErrorContractTests</c> exists to refuse.
/// </para>
/// </remarks>
public sealed class ServiceUnavailableResponseTransformer : IOpenApiOperationTransformer
{
    /// <summary>The body of a money operation's 503: the shared component plus <c>applied</c>.</summary>
    internal const string MoneyComponentName = "MoneyServiceUnavailable";

    private const string Database =
        "Service Unavailable - the database could not be reached or did not answer in time";

    private const string Retry =
        " The body carries errorCode SERVICE_UNAVAILABLE and retryAfterSeconds, the Retry-After "
        + "header carries the same value, and the answer is never cached (its Cache-Control includes no-store). "
        + "Send the request again after that many seconds.";

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var money = metadata?.OfType<RequireIdempotencyAttribute>().Any() == true;

        // The three exempt token endpoints run to completion once started, so they cannot answer
        // for a deadline; saying they could would be a claim the code does not make.
        var cause = metadata?.OfType<NoRequestDeadlineAttribute>().Any() == true
            ? Database + "."
            : Database + ", or the request ran past its deadline.";

        operation.Responses ??= [];

        if (!money)
        {
            ProblemDetailsResponses.Declare(operation.Responses, "503", "Service Unavailable", cause + Retry);
            return Task.CompletedTask;
        }

        AddMoneyComponent(context);
        ProblemDetailsResponses.Declare(
            operation.Responses,
            "503",
            "Service Unavailable",
            cause + Retry
            + " applied: false means this request changed nothing; without applied the outcome is "
            + "unknown. Keep the same Idempotency-Key either way: it is what lets the money move at "
            + "most once.",
            MoneyComponentName);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds the <c>MoneyServiceUnavailable</c> component once, beside the operation that refers to it,
    /// so the reference and its target cannot be registered apart.
    /// </summary>
    private static void AddMoneyComponent(OpenApiOperationTransformerContext context)
    {
        /*
          Loud, not quiet, as ProblemDetailsExtensionsTransformer is: without the document the
          operation would point at a component nobody declares.
        */
        var document = context.Document
            ?? throw new InvalidOperationException(
                $"No document was handed to the operation transformer, so the '{MoneyComponentName}' "
                + "component its 503 refers to cannot be declared.");

        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Components.Schemas.TryAdd(MoneyComponentName, new OpenApiSchema
        {
            Description =
                "The outage 503 of a keyed money operation: the shared ProblemDetails, plus applied.",
            AllOf =
            [
                new OpenApiSchemaReference(ProblemDetailsResponses.ComponentName),
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["applied"] = new OpenApiSchema
                        {
                            // false is the only value ServiceUnavailableExceptionHandler writes, so
                            // it is the only one published: a plain boolean would promise the
                            // generated clients a true that no 503 sends. (Until 2026-10-01 this
                            // said "a true the API never sends". The API does send one since
                            // then: on the 409 IDEMPOTENCY_RESULT_UNKNOWN of a keyed money
                            // operation whose record was read as committed, never on a 503.)
                            Type = JsonSchemaType.Boolean,
                            Enum = [JsonValue.Create(false)],
                            Description =
                                "Present, and false, only when this request held the idempotency "
                                + "claim it made and let no commit start: nothing was changed. "
                                + "Absent when the outcome is unknown: a commit started, or the "
                                + "request failed before it owned the key's claim.",
                        },
                    },
                },
            ],
        });
    }
}
