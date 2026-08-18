using AzureBank.Api.Attributes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI operation transformer that adds 404 Not Found responses to endpoints
/// with path parameters (e.g., /api/accounts/{id}).
///
/// Purpose:
/// - Documents 404 responses for resource lookup endpoints
/// - Fixes Schemathesis "Missing Content-Type header" on 404 responses
/// - ASP.NET Core returns empty 404 without Content-Type, which is valid
///
/// Note: 404 responses are documented with empty body since that's what
/// the API actually returns when a resource is not found.
/// </summary>
public sealed class NotFoundResponseTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        /*
          One endpoint has a path parameter and cannot miss: GET /api/users/{azureTag} answers an
          unknown handle with 200 and exists:false, by design (ADR-0014). Marked at the action rather
          than special-cased by route here, so the claim sits next to the code that makes it true.
        */
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata?.OfType<AlwaysFoundAttribute>().Any() == true)
        {
            return Task.CompletedTask;
        }

        // Check if operation has path parameters (indicates resource lookup)
        var hasPathParameters = operation.Parameters?
            .Any(p => p.In == ParameterLocation.Path) ?? false;

        // Also check route template for path parameters
        var routeTemplate = context.Description.RelativePath ?? "";
        var hasRoutePathParam = routeTemplate.Contains('{');

        if (hasPathParameters || hasRoutePathParam)
        {
            // Ensure Responses dictionary is initialized to avoid null reference warnings
            operation.Responses ??= []; // new OpenApiResponses();

            /*
              FILL IN, NEVER OVERRIDE — the same correction as AuthorizationResponseTransformer, and
              for the same reason.

              This ASSIGNED an empty 404 "even if already defined with content", justified by
              "ASP.NET Core returns empty 404 without Content-Type header … This matches actual
              ASP.NET Core behavior". It does not match THIS application's behaviour: a lookup miss
              goes through GlobalExceptionHandler and comes back as 302 bytes of application/json —
              measured, GET /api/accounts/{unknown-guid}:

                {"type":"https://httpstatuses.com/404","title":"Not Found","status":404,
                 "detail":"Account with identifier '…' was not found.","instance":"/api/accounts/…",
                 "errorCode":"ACCOUNT_NOT_FOUND","traceId":"d23fb0a6631cbb55fc84a32313f15191"}

              A genuinely EMPTY 404 does exist on these routes, and it is a different thing: a
              segment that fails the {id:guid} route constraint matches no route at all, so the
              framework answers before MVC with application/problem+json and a W3C trace-context
              traceId. That is not this response and is not documented as it.
            */
            ProblemDetailsResponses.Declare(
                operation.Responses,
                "404",
                "Not Found",
                "Not Found - the resource does not exist, or is not visible to the caller. The body "
                + "is a ProblemDetails whose errorCode names the resource "
                + "(e.g. ACCOUNT_NOT_FOUND, TRANSACTION_NOT_FOUND).");
        }

        return Task.CompletedTask;
    }
}
