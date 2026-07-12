using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI schema transformer that converts enum types to string enums.
/// This addresses Microsoft.AspNetCore.OpenApi issue #61303 where JsonStringEnumConverter
/// is not automatically respected in the generated OpenAPI schema.
///
/// Purpose:
/// - Ensures enum schemas use "type": "string" instead of "type": "integer"
/// - Includes all enum values in the schema for client code generation
/// - Provides human-readable enum descriptions
///
/// Reference: https://github.com/dotnet/aspnetcore/issues/61303
/// </summary>
public sealed class JsonStringEnumSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;

        // Handle nullable enums (Nullable<T>)
        var enumType = Nullable.GetUnderlyingType(type) ?? type;

        if (enumType.IsEnum)
        {
            // Set type to string (OpenAPI 3.1 format)
            schema.Type = JsonSchemaType.String;

            // Get all enum names and add as JsonArray (.NET 10 / OpenAPI.NET 2.0 requirement)
            // Filter out special values like "Unspecified" that are internal sentinel values
            var enumNames = Enum.GetNames(enumType)
                .Where(name => !name.Equals("Unspecified", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            // Build enum values list - JsonValue.Create returns non-null for non-null string input
            // CS8619 suppression: JsonArray implements IList<JsonNode?> but schema.Enum expects IList<JsonNode>
            // This is a known nullability annotation mismatch in OpenAPI.NET; values are guaranteed non-null
#pragma warning disable CS8619
            schema.Enum = new JsonArray(
                enumNames.Select(name => JsonValue.Create(name)).ToArray()
            );
#pragma warning restore CS8619

            // Preserve existing description or generate default
            if (string.IsNullOrEmpty(schema.Description))
            {
                schema.Description = $"Possible values: {string.Join(", ", enumNames)}";
            }
        }

        return Task.CompletedTask;
    }
}
