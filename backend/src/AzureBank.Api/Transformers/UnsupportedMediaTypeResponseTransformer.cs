using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// Declares the 415 every operation with a request body can answer: the framework refuses a body
/// that is not <c>application/json</c> before model binding, and the document said nothing about it.
/// </summary>
/// <remarks>
/// <para>
/// Found by the first run of the Schemathesis conformance gate, 2026-09-15: fourteen operations,
/// every one that takes a body, answered an "Undocumented HTTP status code". Measured with a
/// <c>text/plain</c> body on <c>POST /api/accounts</c> and on the anonymous
/// <c>POST /api/auth/login</c> alike:
/// </para>
/// <code>
/// HTTP/1.1 415 Unsupported Media Type
/// Content-Type: application/json; charset=utf-8
/// {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.16","title":"Unsupported Media Type",
///  "status":415,"traceId":"00-ebaba1908a516a55f19cbc9d7361e255-c1faaf68191d98bf-01"}
/// </code>
/// <para>
/// A ProblemDetails with no <c>errorCode</c>: MVC's consumes check answers it, not this application,
/// so there is no code to name and the shared schema's optional member stays absent. Declared
/// through <see cref="ProblemDetailsResponses"/> so the body is the shared component, and filled
/// in rather than assigned, as every response transformer here is (ADR-0043).
/// </para>
/// </remarks>
public sealed class UnsupportedMediaTypeResponseTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (operation.RequestBody is null)
        {
            return Task.CompletedTask;
        }

        operation.Responses ??= [];
        ProblemDetailsResponses.Declare(
            operation.Responses,
            "415",
            "Unsupported Media Type",
            "Unsupported Media Type - the request body is not application/json. Refused by the "
            + "framework before model binding, as a ProblemDetails with no errorCode.");
        return Task.CompletedTask;
    }
}
