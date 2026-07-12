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

            // Always set 404 to empty body (even if already defined with content)
            // ASP.NET Core returns empty 404 without Content-Type header
            operation.Responses["404"] = new OpenApiResponse
            {
                Description = "Not Found - The requested resource does not exist."
                // IMPORTANT: No Content property - API returns empty body
                // This matches actual ASP.NET Core behavior
            };
        }

        return Task.CompletedTask;
    }
}
