using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI operation transformer that marks [AllowAnonymous] endpoints as not requiring
/// the global Bearer security scheme.
///
/// Purpose:
/// - Fixes Schemathesis "Missing header not rejected" false positives on anonymous endpoints
/// - Ensures /api/auth/login and /api/auth/register don't show security requirements
/// - Overrides the global security scheme for specific endpoints
///
/// When an endpoint has [AllowAnonymous] attribute, this transformer sets an empty
/// security requirement, indicating that the endpoint accepts unauthenticated requests.
/// </summary>
public sealed class AnonymousEndpointTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;

        if (metadata == null)
        {
            return Task.CompletedTask;
        }

        var hasAllowAnonymous = metadata.OfType<AllowAnonymousAttribute>().Any();

        if (hasAllowAnonymous)
        {
            // Override global security with empty array = no auth required
            // This tells Schemathesis this endpoint is truly anonymous
            operation.Security = new List<OpenApiSecurityRequirement>
            {
                new OpenApiSecurityRequirement() // Empty = no auth required
            };
        }

        return Task.CompletedTask;
    }
}
