using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI operation transformer that adds 400 Bad Request responses to endpoints
/// that accept request bodies (POST, PUT, PATCH operations).
///
/// Purpose:
/// - Documents validation error responses for endpoints with request bodies
/// - Fixes Schemathesis "Missing header not rejected" false positives on anonymous endpoints
/// - Ensures OpenAPI spec accurately reflects FluentValidation behavior
///
/// Note: This transformer adds 400 responses WITHOUT body content specification
/// because the actual ProblemDetails response format is already handled by ASP.NET Core.
/// </summary>
public sealed class ValidationResponseTransformer : IOpenApiOperationTransformer
{
    private static readonly HashSet<string> MethodsWithBody = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST", "PUT", "PATCH"
    };

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        // Ensure Responses dictionary is initialized to avoid null reference warnings
        var responses = operation.Responses ??= new OpenApiResponses();

        var httpMethod = context.Description.HttpMethod;

        // Add 400 to operations that accept request bodies (POST, PUT, PATCH)
        if (httpMethod != null && MethodsWithBody.Contains(httpMethod))
        {
            var hasRequestBody = operation.RequestBody != null;
            if (hasRequestBody && !responses.ContainsKey("400"))
            {
                Add400Response(responses);
            }
        }

        // Also add 400 to GET endpoints with query or path parameters
        // These can fail validation (e.g., invalid format, missing required params)
        if (httpMethod?.Equals("GET", StringComparison.OrdinalIgnoreCase) == true)
        {
            var parameters = operation.Parameters;
            var hasPathParameters = parameters?.Any(p => p.In == ParameterLocation.Path) ?? false;
            var hasQueryParameters = parameters?.Any(p => p.In == ParameterLocation.Query) ?? false;

            if ((hasPathParameters || hasQueryParameters) && !responses.ContainsKey("400"))
            {
                // Path-only endpoints may return empty 400 (framework-level validation)
                // Query endpoints typically return JSON validation errors
                Add400Response(responses, emptyBodyAllowed: hasPathParameters && !hasQueryParameters);
            }
        }

        // Add 400 to DELETE and PATCH endpoints with path parameters but no request body
        // These can fail when path contains invalid UTF-8 or malformed data
        if (httpMethod?.Equals("DELETE", StringComparison.OrdinalIgnoreCase) == true ||
            (httpMethod?.Equals("PATCH", StringComparison.OrdinalIgnoreCase) == true && operation.RequestBody == null))
        {
            var parameters = operation.Parameters;
            var hasPathParameters = parameters?.Any(p => p.In == ParameterLocation.Path) ?? false;

            if (hasPathParameters && !responses.ContainsKey("400"))
            {
                // Path parameter validation may return empty 400 for invalid UTF-8
                Add400Response(responses, emptyBodyAllowed: true);
            }
        }

        return Task.CompletedTask;
    }

    private static void Add400Response(OpenApiResponses responses, bool emptyBodyAllowed = false)
    {
        // If empty body is allowed (path parameter validation), don't specify content
        // This allows both empty and JSON responses to be valid
        if (emptyBodyAllowed)
        {
            responses["400"] = new OpenApiResponse
            {
                Description = "Bad Request - Invalid parameter format or validation failed."
                // No Content - framework may return empty body for invalid path parameters
            };
            return;
        }

        responses["400"] = new OpenApiResponse
        {
            Description = "Bad Request - Validation failed. Check the errors property for details.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                // ASP.NET Core returns application/json for validation errors, not problem+json
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = CreateValidationProblemDetailsSchema()
                }
            }
        };
    }

    /// <summary>
    /// Creates a schema for RFC 7807 ProblemDetails with validation errors extension.
    /// </summary>
    private static OpenApiSchema CreateValidationProblemDetailsSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["type"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "A URI reference identifying the problem type"
                },
                ["title"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "A short, human-readable summary"
                },
                ["status"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer,
                    Description = "The HTTP status code (400)"
                },
                ["errors"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Description = "Validation errors keyed by property name",
                    AdditionalProperties = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Array,
                        Items = new OpenApiSchema { Type = JsonSchemaType.String }
                    }
                },
                ["traceId"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Request trace identifier for debugging"
                }
            }
        };
    }
}
