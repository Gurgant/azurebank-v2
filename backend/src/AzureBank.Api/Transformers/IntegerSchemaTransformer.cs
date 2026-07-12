using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI schema transformer that fixes the .NET 10 number/string union type quirk.
///
/// Problem:
/// .NET 10's OpenAPI generation emits integers as union types: ["integer", "string"]
/// with a pattern that allows string representation. This causes JSON Schema validators
/// (like Schemathesis) to ignore `minimum`/`maximum` constraints when values are sent
/// as strings, because these constraints only apply to numeric types.
///
/// Solution:
/// This transformer removes the string type option from integer and number schemas,
/// ensuring `minimum` and `maximum` constraints are properly enforced.
///
/// References:
/// - https://svrooij.io/2025/12/19/openapi-dotnet-10-number-quirk/
/// - https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/customize-openapi
/// </summary>
public sealed class IntegerSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        // Fix int32 schemas (int, short, etc.)
        if (schema.Format == "int32")
        {
            schema.Type = JsonSchemaType.Integer;
            schema.Pattern = null; // Remove the number-or-string pattern
        }

        // Fix int64 schemas (long)
        if (schema.Format == "int64")
        {
            schema.Type = JsonSchemaType.Integer;
            schema.Pattern = null;
        }

        // Fix double schemas (decimal, double, float)
        if (schema.Format == "double" || schema.Format == "float")
        {
            schema.Type = JsonSchemaType.Number;
            schema.Pattern = null;
        }

        return Task.CompletedTask;
    }
}
