using System.ComponentModel.DataAnnotations;
using System.Reflection;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Validation;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI schema transformer that applies Data Annotation constraints to generated schemas.
///
/// Purpose:
/// - Ensures OpenAPI schemas reflect actual validation rules
/// - Fixes Schemathesis "API rejected schema-compliant request" errors
/// - Reads [Required], [StringLength], [Range], [EmailAddress], [RegularExpression] attributes
/// - Handles custom AzureBank validation attributes: [Password], [AzureTag], [Pin]
///
/// This transformer examines the .NET type and its properties, extracting validation
/// attributes and converting them to OpenAPI schema constraints (minLength, maxLength,
/// minimum, maximum, format, pattern).
/// </summary>
public sealed class DataAnnotationSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;

        // Skip if not a class or has no properties
        if (!type.IsClass || type == typeof(string))
        {
            // Handle string properties with validation attributes
            ApplyStringConstraints(schema, context);
            return Task.CompletedTask;
        }

        // For complex types, process each property
        if (schema.Properties != null)
        {
            foreach (var (propertyName, propertySchema) in schema.Properties)
            {
                // Find the matching property on the type (case-insensitive for camelCase)
                var property = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase));

                if (property != null && propertySchema is OpenApiSchema openApiSchema)
                {
                    ApplyPropertyConstraints(openApiSchema, property);
                }
            }
        }

        return Task.CompletedTask;
    }

    private static void ApplyStringConstraints(OpenApiSchema schema, OpenApiSchemaTransformerContext context)
    {
        // This method handles simple string type schemas
        // Property-level constraints are handled in ApplyPropertyConstraints
    }

    private static void ApplyPropertyConstraints(OpenApiSchema schema, PropertyInfo property)
    {
        var attributes = property.GetCustomAttributes(true);

        foreach (var attribute in attributes)
        {
            switch (attribute)
            {
                // Required - mark as required (not nullable)
                case RequiredAttribute:
                    // Required is handled at the parent schema level
                    break;

                // StringLength - adds minLength and maxLength
                case StringLengthAttribute stringLength:
                    if (stringLength.MinimumLength > 0)
                    {
                        schema.MinLength = stringLength.MinimumLength;
                    }
                    if (stringLength.MaximumLength > 0)
                    {
                        schema.MaxLength = stringLength.MaximumLength;
                    }
                    break;

                // MinLength - adds minLength only
                case MinLengthAttribute minLength:
                    schema.MinLength = minLength.Length;
                    break;

                // MaxLength - adds maxLength only
                case MaxLengthAttribute maxLength:
                    schema.MaxLength = maxLength.Length;
                    break;

                // Range - adds minimum and maximum for numeric types
                // OpenAPI.NET 2.0 uses string for min/max (JSON Schema compatibility)
                case RangeAttribute range:
                    if (range.Minimum != null)
                    {
                        schema.Minimum = Convert.ToDecimal(range.Minimum).ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                    }
                    if (range.Maximum != null)
                    {
                        schema.Maximum = Convert.ToDecimal(range.Maximum).ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                    }
                    break;

                // EmailAddress - adds format: email
                case EmailAddressAttribute:
                    schema.Format = "email";
                    // Also add minLength:1 since email can't be empty
                    schema.MinLength ??= 1;
                    break;

                // RegularExpression - adds pattern
                case RegularExpressionAttribute regex:
                    schema.Pattern = regex.Pattern;
                    break;

                // Phone - adds format: phone
                case PhoneAttribute:
                    schema.Format = "phone";
                    break;

                // Url - adds format: uri
                case UrlAttribute:
                    schema.Format = "uri";
                    break;

                // CreditCard - adds format
                case CreditCardAttribute:
                    schema.Format = "credit-card";
                    break;

                // ═══════════════════════════════════════════════════════════
                // Custom AzureBank validation attributes
                // ═══════════════════════════════════════════════════════════

                // Password - minLength: 8, maxLength: 128, pattern for complexity
                case PasswordAttribute:
                    schema.MinLength = ValidationRules.PasswordMinLength;
                    schema.MaxLength = ValidationRules.PasswordMaxLength;
                    schema.Pattern = ValidationRules.PasswordPattern;
                    schema.Description = "Password must contain at least one uppercase, one lowercase, and one digit.";
                    break;

                // AzureTag - minLength: 3, maxLength: 20, pattern for format
                case AzureTagAttribute:
                    schema.MinLength = ValidationRules.AzureTagMinLength;
                    schema.MaxLength = ValidationRules.AzureTagMaxLength;
                    schema.Pattern = ValidationRules.AzureTagPattern;
                    schema.Description = "Must start with a letter and contain only lowercase letters, numbers, and underscores.";
                    break;

                // Pin - exactly 6 digits
                case PinAttribute:
                    schema.MinLength = ValidationRules.PinLength;
                    schema.MaxLength = ValidationRules.PinLength;
                    schema.Pattern = ValidationRules.PinPattern;
                    schema.Description = "PIN must be exactly 6 digits.";
                    break;

                // NotEmptyGuid - UUID cannot be empty (00000000-0000-0000-0000-000000000000)
                case NotEmptyGuidAttribute:
                    schema.Format = "uuid";
                    schema.Description = "A valid non-empty UUID is required.";
                    break;

                // MoneyRange - amount must be between min and max transaction limits
                // OpenAPI 3.1: Use minimum only (inclusive). Value >= 0.01 means 0 is rejected.
                // Do NOT use exclusiveMinimum - it creates ambiguity with minimum in OpenAPI 3.1
                case MoneyRangeAttribute:
                    schema.Minimum = ValidationRules.TransactionMinAmount.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    schema.Maximum = ValidationRules.TransactionMaxAmount.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    schema.MultipleOf = 0.01m;
                    schema.Description = $"Amount must be between ${ValidationRules.TransactionMinAmount} and ${ValidationRules.TransactionMaxAmount:N2}.";
                    break;
            }
        }

        // If property is marked [Required] and is a string, ensure minLength >= 1
        var hasRequired = attributes.OfType<RequiredAttribute>().Any();
        if (hasRequired && property.PropertyType == typeof(string))
        {
            schema.MinLength ??= 1;
        }
    }
}
