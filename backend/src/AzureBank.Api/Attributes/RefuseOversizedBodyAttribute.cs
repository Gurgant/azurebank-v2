using AzureBank.Api.Middleware;
using AzureBank.Shared.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Api.Attributes;

/// <summary>
/// Answers 413 <c>PAYLOAD_TOO_LARGE</c> when the action it marks is sent a body over the limit its
/// endpoint declares: the four authorisation mints, 32,768 bytes each. A resource filter, so it
/// runs after authentication: with no token the 401 still comes first.
/// </summary>
/// <remarks>
/// A body whose <c>Content-Length</c> is over the limit is read and thrown away first by
/// <see cref="OversizedBodyDrain"/>, the type <see cref="IdempotencyMiddleware"/> uses, on the
/// host's clock: up to 1 MiB, and for five seconds at most. When all of it came, the 413 does not
/// say <c>Connection: close</c> and the connection can be kept. Above that size the body is not
/// read, and once the five seconds are up the rest is not waited for: either way the 413 says
/// <c>Connection: close</c>. A chunked body has no length to check first: the server's own 413
/// during the read becomes the same refusal, with the rest of the body unread and
/// <c>Connection: close</c>. Any other refusal of the server's is left as it is. Held on Kestrel
/// by <c>KestrelRequestSizeLimitTests</c> and in memory by <c>MintOversizedBodyDrainTests</c>; on
/// Kestrel no test sends a mint more than 1 MiB or stalls a body.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RefuseOversizedBodyAttribute : Attribute, IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        var limit = httpContext.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;

        if (limit is not null && httpContext.Request.ContentLength > limit.Value)
        {
            var timeProvider = httpContext.RequestServices.GetRequiredService<TimeProvider>();
            await new OversizedBodyDrain(timeProvider).DrainOrCloseAsync(httpContext);
            throw new PayloadTooLargeException();
        }

        var executedContext = await next();

        if (executedContext.Exception is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            executedContext.Exception = new PayloadTooLargeException();
        }
    }
}
