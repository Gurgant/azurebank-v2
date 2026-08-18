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

        /*
          A QUERY parameter can fail binding, so a GET that has one can genuinely answer 400.

          A PATH parameter cannot, and the two branches that used to say so are deleted rather than
          corrected. Their reasoning was "path-only endpoints may return empty 400 (framework-level
          validation)" and "path parameter validation may return empty 400 for invalid UTF-8".
          Measured on the running API, all four such routes answer 404 instead:

            GET   /api/accounts/not-a-guid              404 application/problem+json
            GET   /api/accounts/not-a-guid/full-number  404 application/problem+json
            GET   /api/transactions/not-a-guid          404 application/problem+json
            PATCH /api/accounts/not-a-guid/set-primary  404 application/problem+json

          The cause is that every one of them is declared [HttpGet("{id:guid}")] and a route
          constraint participates in route MATCHING, not in binding: a non-GUID segment matches no
          route at all, so the framework answers before MVC is entered and no model binding — hence
          no binding 400 — ever happens. A path parameter with no constraint cannot fail either, for
          the opposite reason: every byte sequence is a valid string.

          So those four declarations described a response the API cannot produce, which is the same
          defect as an undeclared body pointed the other way.
        */
        if (httpMethod?.Equals("GET", StringComparison.OrdinalIgnoreCase) == true)
        {
            var hasQueryParameters =
                operation.Parameters?.Any(p => p.In == ParameterLocation.Query) ?? false;

            if (hasQueryParameters && !responses.ContainsKey("400"))
            {
                Add400Response(responses);
            }
        }

        return Task.CompletedTask;
    }

    // The emptyBodyAllowed branch went with the two path-only callers above: nothing reaches this
    // without a body any more, so the parameter is gone rather than left defaulting to a dead path.
    private static void Add400Response(OpenApiResponses responses)
    {
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
