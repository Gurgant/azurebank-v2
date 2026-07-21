using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI schema transformer that marks non-nullable VALUE-TYPE properties as required.
///
/// Purpose:
/// - System.Text.Json ALWAYS serializes non-nullable value types (Guid, decimal, bool,
///   DateTime, enums) — they can never be absent on the wire — but the generator only
///   marks C# `required` members as required, so the published contract claimed
///   `id?: string` for a Guid that is always present.
/// - Downstream, that looseness forced the frontend to treat guaranteed fields as
///   possibly missing (`account.id ?? ''`) and fed review bots a stream of
///   "guard against undefined id" false positives.
///
/// Nullable value types (DateTime?, decimal?) keep their optionality — those CAN be
/// null/absent by contract.
/// </summary>
public sealed class RequiredValueTypeMembersTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (schema.Properties is not { Count: > 0 })
        {
            return Task.CompletedTask;
        }

        // JsonTypeInfo.Properties is the serializer's OWN precomputed metadata: it
        // carries the exact wire name (naming policy and [JsonPropertyName] applied)
        // and already excludes ignored members — no manual reflection, no name guessing.
        if (context.JsonTypeInfo?.Properties is not { } jsonProperties)
        {
            return Task.CompletedTask;
        }

        foreach (var jsonProperty in jsonProperties)
        {
            var propertyType = jsonProperty.PropertyType;
            var isNonNullableValueType =
                propertyType.IsValueType && Nullable.GetUnderlyingType(propertyType) == null;
            if (!isNonNullableValueType)
            {
                continue;
            }

            if (!schema.Properties.ContainsKey(jsonProperty.Name))
            {
                continue;
            }

            schema.Required ??= new HashSet<string>();
            schema.Required.Add(jsonProperty.Name);
        }

        return Task.CompletedTask;
    }
}
