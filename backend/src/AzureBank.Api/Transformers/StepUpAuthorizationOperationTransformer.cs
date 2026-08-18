using AzureBank.Api.Attributes;
using AzureBank.Shared.Constants;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// Publishes the <c>Step-Up-Authorization</c> header as REQUIRED on [RequireStepUpAuthorization]
/// endpoints (ADR-0042).
///
/// <para>
/// It MUTATES the parameter MVC already emitted from <c>[FromHeader]</c> rather than adding one,
/// which is the difference from <c>IdempotencyOperationTransformer</c>: the idempotency key has no
/// action parameter at all, so that transformer creates the entry; this one only flips a flag the
/// generator got from nullability.
/// </para>
///
/// <para>
/// And nullability is exactly why this exists. The parameter is <c>Guid?</c> on purpose, so that an
/// absent header and an empty one alike reach the service and receive the promised
/// <c>401 AUTHORIZATION_REQUIRED</c>; making it non-nullable would buy a `required: true` in the
/// document at the price of replacing that 401 with a model-state 400 carrying no <c>errorCode</c>.
/// Left alone, the emitted parameter carries no <c>required</c> key at all — measured — so the
/// published contract said the header was optional while the API refused without it. Nothing would
/// have caught that: the drift gate compares the generated artefacts to the document, and both were
/// wrong in the same direction.
/// </para>
/// </summary>
public sealed class StepUpAuthorizationOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata is null || !metadata.OfType<RequireStepUpAuthorizationAttribute>().Any())
        {
            return Task.CompletedTask;
        }

        // The list is typed as the read-only interface; the concrete type is what carries a settable
        // Required. A miss on either the lookup or the cast is fatal below rather than silent.
        var parameter = operation.Parameters?.FirstOrDefault(p =>
            p.In == ParameterLocation.Header
            && string.Equals(p.Name, StepUpConstants.HeaderName, StringComparison.OrdinalIgnoreCase))
            as OpenApiParameter;

        /*
          A marker on an action whose header parameter is gone would silently document nothing, which
          is the failure this whole transformer exists to prevent — so say so instead of returning
          quietly. The architecture suite has the same shape of assertion for its source scans.
        */
        if (parameter is null)
        {
            throw new InvalidOperationException(
                $"[RequireStepUpAuthorization] is on {context.Description.RelativePath} but no "
                + $"'{StepUpConstants.HeaderName}' header parameter was emitted for it. Either the "
                + "[FromHeader] parameter was removed or renamed, or the marker is on the wrong "
                + "action; both would publish a contract that omits a header the API requires.");
        }

        parameter.Required = true;
        return Task.CompletedTask;
    }
}
