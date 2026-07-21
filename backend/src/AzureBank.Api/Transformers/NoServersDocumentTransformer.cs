using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI document transformer that strips the servers block.
///
/// The generator stamps the REQUESTING host (e.g. http://localhost:5068) into `servers`,
/// which is environment noise in a committed contract and breaks regen idempotency —
/// the file would differ depending on which profile served the curl.
/// </summary>
public sealed class NoServersDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Servers = null;
        return Task.CompletedTask;
    }
}
