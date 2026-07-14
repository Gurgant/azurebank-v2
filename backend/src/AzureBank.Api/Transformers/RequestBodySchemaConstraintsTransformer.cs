using System.Reflection;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Validation;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI document transformer that applies validation constraints to request body schemas.
///
/// Purpose:
/// - Fixes .NET 10's IOpenApiSchemaTransformer limitation where property-level attributes
///   don't persist when modifying schema.Properties (due to shared schema references)
/// - Applies constraints from custom validation attributes: [MoneyRange], [Password], [AzureTag], [Pin]
/// - Runs after all schemas are finalized, modifying components/schemas directly
///
/// This transformer maps schema names to DTO types, then applies validation attribute
/// constraints to the schema properties.
/// </summary>
public sealed class RequestBodySchemaConstraintsTransformer : IOpenApiDocumentTransformer
{
    // Maps OpenAPI schema names to their corresponding .NET types
    // This is necessary because at document-transform time, we don't have type metadata
    private static readonly Dictionary<string, Type> SchemaTypeMapping = new()
    {
        // Transaction DTOs
        ["DepositRequest"] = typeof(AzureBank.Shared.DTOs.Transaction.DepositRequest),
        ["WithdrawRequest"] = typeof(AzureBank.Shared.DTOs.Transaction.WithdrawRequest),

        // Transfer DTOs
        ["InternalTransferRequest"] = typeof(AzureBank.Shared.DTOs.Transfer.InternalTransferRequest),
        ["TransferRequest"] = typeof(AzureBank.Shared.DTOs.Transfer.TransferRequest),

        // Auth DTOs
        ["RegisterRequest"] = typeof(AzureBank.Shared.DTOs.Auth.RegisterRequest),
        ["LoginRequest"] = typeof(AzureBank.Shared.DTOs.Auth.LoginRequest),
        ["SetPinRequest"] = typeof(AzureBank.Shared.DTOs.Auth.SetPinRequest),
        ["VerifyPinRequest"] = typeof(AzureBank.Shared.DTOs.Auth.VerifyPinRequest),

        // Account DTOs
        ["CreateAccountRequest"] = typeof(AzureBank.Shared.DTOs.Account.CreateAccountRequest),
    };

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var schemas = document.Components?.Schemas;
        if (schemas == null) return Task.CompletedTask;

        foreach (var (schemaName, schema) in schemas)
        {
            // Cast to concrete type - schemas values are IOpenApiSchema
            if (schema is not OpenApiSchema concreteSchema) continue;

            // Check if we have a type mapping for this schema
            if (!SchemaTypeMapping.TryGetValue(schemaName, out var dtoType))
                continue;

            // Apply constraints from validation attributes
            ApplySchemaConstraints(concreteSchema, dtoType);
        }

        return Task.CompletedTask;
    }

    private static void ApplySchemaConstraints(OpenApiSchema schema, Type dtoType)
    {
        if (schema.Properties == null) return;

        foreach (var (propertyName, propertySchema) in schema.Properties)
        {
            // Cast to concrete type - properties values are IOpenApiSchema
            if (propertySchema is not OpenApiSchema concretePropertySchema) continue;

            // Find the matching property on the DTO (case-insensitive for camelCase)
            var property = dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase));

            if (property == null) continue;

            ApplyPropertyConstraints(concretePropertySchema, property);
        }
    }

    private static void ApplyPropertyConstraints(OpenApiSchema schema, PropertyInfo property)
    {
        var attributes = property.GetCustomAttributes(true);

        foreach (var attribute in attributes)
        {
            switch (attribute)
            {
                // MoneyRange - amount must be between min and max transaction limits
                case MoneyRangeAttribute:
                    schema.Minimum = ValidationRules.TransactionMinAmount.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    schema.Maximum = ValidationRules.TransactionMaxAmount.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    // Whole-cent constraint so the documented schema matches the <=2-decimal
                    // runtime rule (keeps Schemathesis positive cases from generating sub-cents).
                    schema.MultipleOf = 0.01m;
                    // Remove the string type option for cleaner numeric schema
                    if (schema.Type?.HasFlag(JsonSchemaType.String) == true)
                    {
                        schema.Type = JsonSchemaType.Number;
                        schema.Pattern = null; // Remove the number-or-string pattern
                    }
                    break;

                // Password - minLength: 8, maxLength: 128, pattern for complexity
                case PasswordAttribute:
                    schema.MinLength = ValidationRules.PasswordMinLength;
                    schema.MaxLength = ValidationRules.PasswordMaxLength;
                    schema.Pattern = ValidationRules.PasswordPattern;
                    break;

                // AzureTag - minLength: 3, maxLength: 20, pattern for format
                case AzureTagAttribute:
                    schema.MinLength = ValidationRules.AzureTagMinLength;
                    schema.MaxLength = ValidationRules.AzureTagMaxLength;
                    schema.Pattern = ValidationRules.AzureTagPattern;
                    break;

                // Pin - exactly 6 digits
                case PinAttribute:
                    schema.MinLength = ValidationRules.PinLength;
                    schema.MaxLength = ValidationRules.PinLength;
                    schema.Pattern = ValidationRules.PinPattern;
                    break;
            }
        }
    }
}
