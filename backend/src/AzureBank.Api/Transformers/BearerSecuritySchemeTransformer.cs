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
                Description = "Enter your JWT token. Get a token by calling POST /api/auth/login"
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
