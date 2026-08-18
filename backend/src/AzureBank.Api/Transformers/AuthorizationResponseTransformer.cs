using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI operation transformer that adds 401 Unauthorized and 403 Forbidden responses
/// to all endpoints that require authentication.
///
/// Purpose:
/// - Ensures OpenAPI spec correctly documents authentication requirements
/// - Fixes Schemathesis "Undocumented HTTP status code: 401" errors
/// - Automatically detects [Authorize] attribute on controllers and actions
/// - Declares the ProblemDetails body those responses actually carry, and yields to any endpoint
///   that documents its own (see the note at the assignment)
///
/// This transformer examines endpoint metadata to determine if authentication is required,
/// then adds appropriate response documentation to the OpenAPI schema.
/// </summary>
public sealed class AuthorizationResponseTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        // Check if the endpoint requires authorization
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;

        if (metadata == null)
        {
            return Task.CompletedTask;
        }

        var hasAuthorize = metadata.OfType<AuthorizeAttribute>().Any();
        var hasAllowAnonymous = metadata.OfType<AllowAnonymousAttribute>().Any();

        // If endpoint requires auth (has [Authorize] but not [AllowAnonymous])
        if (hasAuthorize && !hasAllowAnonymous)
        {
            // Ensure Responses dictionary is initialized to avoid null reference warnings
            operation.Responses ??= new OpenApiResponses();

            /*
              FILL IN, NEVER OVERRIDE — and both halves of that were wrong here until now.

              This used to ASSIGN both entries with no body, justified by "ASP.NET Core JWT Bearer
              middleware returns empty responses without Content-Type". That is true of the DEFAULT
              and this application does not use it: ServiceCollectionExtensions calls
              context.HandleResponse() on OnChallenge — whose entire purpose is to suppress that
              default — and then writes JSON carrying errorCode and traceId, and does the same on
              OnForbidden. Measured against the running API, a 401 is 243 bytes of
              application/json with seven keys, and a 403 is 260.

              The assignment was the worse half. TransferController declares
              [ProducesResponseType(typeof(ProblemDetails), 401)] with a comment explaining the
              three step-up codes it can return, and this line threw that away — which is why
              fifteen responses elsewhere carry hand-written INLINE schemas instead of pointing at
              the shared component. Declare lets an endpoint that knows more say more.
            */
            ProblemDetailsResponses.Declare(
                operation.Responses,
                "401",
                "Unauthorized",
                "Unauthorized - authentication failed or is missing. The body is a ProblemDetails "
                + "whose errorCode names the reason (e.g. AUTH_TOKEN_MISSING, AUTH_TOKEN_INVALID).");

            ProblemDetailsResponses.Declare(
                operation.Responses,
                "403",
                "Forbidden",
                "Forbidden - authenticated, but not permitted to reach this resource "
                + "(errorCode: ACCESS_DENIED).");
        }

        return Task.CompletedTask;
    }
}
