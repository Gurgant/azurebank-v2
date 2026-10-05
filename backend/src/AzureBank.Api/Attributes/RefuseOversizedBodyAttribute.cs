using AzureBank.Api.Middleware;
using AzureBank.Shared.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Api.Attributes;

/// <summary>
/// Refuses an oversized request body on a PIN mint with 413 PAYLOAD_TOO_LARGE after draining it,
/// preserving the connection for subsequent requests.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RefuseOversizedBodyAttribute : Attribute, IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        var limit = httpContext.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize
            ?? context.ActionDescriptor.EndpointMetadata.OfType<IRequestSizeLimitMetadata>().FirstOrDefault()?.MaxRequestBodySize;

        if (limit is not null && httpContext.Request.ContentLength > limit.Value)
        {
            var timeProvider = httpContext.RequestServices.GetRequiredService<TimeProvider>();
            await new OversizedBodyDrain(timeProvider).DrainOrCloseAsync(httpContext);
            throw new PayloadTooLargeException();
        }

        var executedContext = await next();

        if (executedContext.Exception is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }
            || executedContext.Exception?.InnerException is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            executedContext.Exception = new PayloadTooLargeException();
        }
    }
}
