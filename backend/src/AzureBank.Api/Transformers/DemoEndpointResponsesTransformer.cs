using AzureBank.Api.Attributes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// Declares the 404 an endpoint of the public demo answers on a deployment that is not the demo
/// (<see cref="DemoOnlyAttribute"/>).
/// </summary>
/// <remarks>
/// <para>
/// The document is generated from the code, whatever <c>Demo:Enabled</c> says where it is
/// generated, so a demo-only operation is in it for every deployment. On most of them the
/// operation answers nothing but this 404, and a client generated from the document has to be
/// told so.
/// </para>
/// <para>
/// AS <c>application/problem+json</c>, and that media type alone: the 404 is not this
/// application's refusal. <c>DemoEndpointMiddleware</c> sets the status and writes nothing, and the
/// status-code pages fill it as they fill a path that matches no route, with no
/// <c>errorCode</c>. It is the answer <see cref="NotFoundResponseTransformer"/> declares as a
/// second media type on the operations whose route constraint can miss; here it is the only one.
/// </para>
/// <para>
/// Assigned, not filled in: nothing else declares a 404 on an operation with no path parameter,
/// and one that an action declared by hand would say <c>application/json</c>, which this answer
/// never is.
/// </para>
/// </remarks>
public sealed class DemoEndpointResponsesTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata?.OfType<DemoOnlyAttribute>().Any() != true)
        {
            return Task.CompletedTask;
        }

        operation.Responses ??= [];
        operation.Responses["404"] = new OpenApiResponse
        {
            Description =
                "Not Found - the demo is off on this deployment (Demo:Enabled is false), where this "
                + "endpoint does not exist. Answered as a path that matches no route is: by the "
                + "framework, as application/problem+json with no errorCode, whatever the request's body.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchemaReference(ProblemDetailsResponses.ComponentName),
                },
            },
        };

        return Task.CompletedTask;
    }
}
