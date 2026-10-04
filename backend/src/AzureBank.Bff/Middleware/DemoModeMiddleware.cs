using System.Diagnostics;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Middleware;

/// <summary>
/// Marks an endpoint of the BFF's own that the public demo closes. While <c>Demo:Enabled</c> is
/// true, <see cref="DemoModeMiddleware"/> reads it from the endpoint metadata and refuses the
/// request with 403, whatever it carries.
/// </summary>
/// <remarks>
/// One endpoint carries it, registration: on the demo a visitor is handed a prepared copy and
/// nobody creates a user. The API has a marker of the same name on its own registration
/// (<c>AzureBank.Api.Attributes.ClosedInDemoAttribute</c>); the two hosts share no web project.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ClosedInDemoAttribute : Attribute
{
}

/// <summary>
/// Refuses with 403 an endpoint the demo closes (<see cref="ClosedInDemoAttribute"/>) while
/// <c>Demo:Enabled</c> is true.
/// </summary>
/// <remarks>
/// <para>
/// <b>403, in the API's own words.</b> The API refuses a registration on the demo with 403, the
/// code <c>REGISTRATION_CLOSED</c> and one sentence. The BFF answers the same code and the same
/// sentence without asking it: the body is written here, in the shape the API's refusals have
/// (<c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c>, <c>instance</c>, <c>errorCode</c>,
/// <c>traceId</c>), as <see cref="FetchMetadataMiddleware"/> writes its own 403. <c>instance</c>
/// is the path the browser asked for.
/// </para>
/// <para>
/// <b>Before model binding.</b> The marker is read from the endpoint's metadata, so the answer
/// does not depend on the body: a malformed body is not answered 400, nor a form post 415, each
/// of which would tell the caller what a registration should look like on a deployment that takes
/// none.
/// </para>
/// <para>
/// <b>After the rate limiter.</b> A closed door is still a door somebody can hammer: a request to
/// it spends the <c>auth</c> policy's allowance like any other, and past the limit the answer is
/// the limiter's 429.
/// </para>
/// <para>
/// <b>The endpoint is closed, not its path.</b> Another method on the path never reaches the
/// endpoint, so no marker is read: the answer is 405 with <c>Allow</c> naming the endpoint's
/// method, with the demo off or on, as the path of sign-in answers
/// (<c>DemoClaimTests.AnotherMethodOnRegistrationsPath_...</c> pins it, for six methods).
/// </para>
/// <para>
/// <b>The flag is read once,</b> when the pipeline is built: <c>Demo:Enabled</c> is a deployment's
/// setting, checked at start, and nothing changes it while the host runs.
/// </para>
/// </remarks>
public sealed class DemoModeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _demoEnabled;

    public DemoModeMiddleware(RequestDelegate next, IOptions<DemoOptions> demo)
    {
        _next = next;
        _demoEnabled = demo.Value.Enabled;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata;
        if (_demoEnabled && metadata?.GetMetadata<ClosedInDemoAttribute>() is not null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = RegistrationClosedException.Detail,
                Type = "https://httpstatuses.com/403",
                Instance = context.Request.Path,
            };
            problem.Extensions["errorCode"] = ErrorCodes.RegistrationClosed;
            // Bare 32-hex trace id: it pastes straight into Tempo/Grafana search.
            problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            return context.Response.WriteAsJsonAsync(problem, context.RequestAborted);
        }

        return _next(context);
    }
}

/// <summary>
/// Extension method for registering the middleware.
/// </summary>
public static class DemoModeMiddlewareExtensions
{
    /// <summary>
    /// Refuses the endpoints the demo closes while the demo is on. After the rate limiter, before
    /// the controllers bind anything.
    /// </summary>
    public static IApplicationBuilder UseDemoMode(this IApplicationBuilder app) =>
        app.UseMiddleware<DemoModeMiddleware>();
}
