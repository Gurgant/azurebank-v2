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

            // Always set 401/403 with empty body (override any existing definitions)
            // ASP.NET Core JWT Bearer middleware returns empty responses without Content-Type
            operation.Responses["401"] = new OpenApiResponse
            {
                Description = "Unauthorized - Authentication required. Provide a valid JWT Bearer token."
                // IMPORTANT: No Content property - JWT Bearer returns empty body
            };

            operation.Responses["403"] = new OpenApiResponse
            {
                Description = "Forbidden - You don't have permission to access this resource."
                // IMPORTANT: No Content property - returns empty body
            };
        }

        return Task.CompletedTask;
    }
}
