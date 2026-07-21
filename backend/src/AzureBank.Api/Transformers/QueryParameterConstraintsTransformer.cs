using AzureBank.Shared.Constants;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI document transformer that adds constraints to query parameters.
///
/// Purpose:
/// - Ensures query parameter schemas have proper min/max constraints
/// - Fixes Schemathesis "API rejected schema-compliant request" for PageSize=0
/// - Query parameters may not go through the standard schema transformer
/// </summary>
public sealed class QueryParameterConstraintsTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        foreach (var path in document.Paths)
        {
            // Skip if Operations dictionary is null
            if (path.Value.Operations is not { } operations)
                continue;

            foreach (var operation in operations.Values)
            {
                if (operation.Parameters == null) continue;

                foreach (var parameter in operation.Parameters)
                {
                    // Cast to concrete type for modification
                    if (parameter is not OpenApiParameter openApiParam) continue;
                    // Path parameters carry the same domain constraints as their query
                    // twins (e.g. GET /api/users/{azureTag}) — without them a regen
                    // silently DROPS the documented pattern/length rules.
                    if (openApiParam.In is not (ParameterLocation.Query or ParameterLocation.Path))
                        continue;
                    if (openApiParam.Schema is not OpenApiSchema schema) continue;

                    ApplyParameterConstraints(openApiParam, schema);
                }
            }
        }

        return Task.CompletedTask;
    }

    private static void ApplyParameterConstraints(OpenApiParameter parameter, OpenApiSchema schema)
    {
        switch (parameter.Name?.ToLowerInvariant())
        {
            case "pagesize":
                schema.Minimum = ValidationRules.MinPageSize.ToString();
                schema.Maximum = ValidationRules.MaxPageSize.ToString();
                schema.Default = ValidationRules.DefaultPageSize;
                break;

            case "page":
                schema.Minimum = ValidationRules.MinPage.ToString();
                schema.Default = ValidationRules.DefaultPage;
                break;

            case "azuretag":
                // azureTag query parameter should be required and have min length
                parameter.Required = true;
                schema.MinLength = ValidationRules.AzureTagMinLength;
                schema.MaxLength = ValidationRules.AzureTagMaxLength;
                schema.Pattern = ValidationRules.AzureTagPattern;
                break;
        }
    }
}
