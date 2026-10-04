using AzureBank.Api.Attributes;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Middleware;

/// <summary>
/// Answers 404 to an endpoint that exists only on the public demo
/// (<see cref="DemoOnlyAttribute"/>) while <c>Demo:Enabled</c> is false, and refuses with 403 an
/// endpoint the demo closes (<see cref="ClosedInDemoAttribute"/>) while it is true.
/// </summary>
/// <remarks>
/// <para>
/// <b>404, as a path that matches no route.</b> A deployment that is not the demo should not
/// answer for the demo's endpoints, so the response is left empty for the status-code pages to fill,
/// which is what they do for a path with no route, and what <see cref="TokenRoadMiddleware"/> does
/// for a caller off the road. The two answers are the same but for the trace id
/// (<c>DemoModeEndpointTests</c> compares them through the host).
/// </para>
/// <para>
/// <b>The endpoint is hidden, not its path.</b> Another method on a demo-only endpoint's path
/// never reaches that endpoint, so no marker is read: the answer is 405 with <c>Allow</c> naming
/// the endpoint's method, with the demo off or on, as the path of sign-in answers
/// (<c>DemoModeEndpointTests.AnotherMethodOnTheClaimsPath_...</c> pins it, for six methods). A
/// path with no route is 404 under each of them, so the 405 tells that this build has the
/// endpoint. It does not tell whether the demo is on.
/// </para>
/// <para>
/// <b>403, as a refusal of the application's.</b> On the demo registration is closed, and says
/// so: the middleware throws <see cref="RegistrationClosedException"/>, and the exception handler
/// above it writes the 403 with its code, its one sentence and a trace id, as it writes every
/// refusal. Only on the token road: a caller off it was answered 404 by
/// <see cref="TokenRoadMiddleware"/> before this one ran. Another method on the path is 405 here
/// too, with the demo off or on
/// (<c>DemoModeEndpointTests.AnotherMethodOnRegistrationsPath_...</c>).
/// </para>
/// <para>
/// <b>Before model binding.</b> A marker is read from the endpoint's metadata, so neither answer
/// depends on the body: a malformed body is not answered 400, nor a form post 415.
/// </para>
/// <para>
/// <b>The flag is read once,</b> when the pipeline is built: <c>Demo:Enabled</c> is a deployment's
/// setting, checked at start, and nothing changes it while the host runs.
/// </para>
/// </remarks>
public sealed class DemoEndpointMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _demoEnabled;

    public DemoEndpointMiddleware(RequestDelegate next, IOptions<DemoOptions> demo)
    {
        _next = next;
        _demoEnabled = demo.Value.Enabled;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata;
        if (!_demoEnabled && metadata?.GetMetadata<DemoOnlyAttribute>() is not null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        if (_demoEnabled && metadata?.GetMetadata<ClosedInDemoAttribute>() is not null)
        {
            throw new RegistrationClosedException();
        }

        return _next(context);
    }
}

/// <summary>
/// Extension method for registering the middleware.
/// </summary>
public static class DemoEndpointMiddlewareExtensions
{
    /// <summary>
    /// Refuses the demo's own endpoints while the demo is off, and the endpoints the demo closes
    /// while it is on. After the token road, before authentication.
    /// </summary>
    public static IApplicationBuilder UseDemoEndpoints(this IApplicationBuilder app) =>
        app.UseMiddleware<DemoEndpointMiddleware>();
}
