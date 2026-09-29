using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI document transformer that adds JWT Bearer authentication scheme.
/// This enables the "Configure" auth button in Scalar UI.
///
/// Updated for Microsoft.OpenApi 2.0 API changes:
/// - Security property instead of SecurityRequirements
/// - OpenApiSecuritySchemeReference instead of Reference property
/// </summary>
public sealed class BearerSecuritySchemeTransformer(IAuthenticationSchemeProvider authSchemeProvider)
    : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var authSchemes = await authSchemeProvider.GetAllSchemesAsync();

        if (authSchemes.Any(s => s.Name == "Bearer"))
        {
            // Add security scheme definition to Components
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                // Scalar shows this beside "Try it". Login answers only the BFF's road (ADR-0055 and
                // TokenRoadMiddleware): a call without the key gets 401, one without the marker or
                // off loopback gets 404, so the text names both headers.
                Description = "Enter your JWT token. Get one from POST /api/auth/login, called from the "
                    + "API's own machine (loopback) with the X-AzureBank-Service-Key header and exactly "
                    + "one X-AzureBank-Token-Road header."
            };

            // Apply security requirement globally using OpenApiSecuritySchemeReference
            document.Security ??= new List<OpenApiSecurityRequirement>();
            document.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>()
            });
        }
    }
}
