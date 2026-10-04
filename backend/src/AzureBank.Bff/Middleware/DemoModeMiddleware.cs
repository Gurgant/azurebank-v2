using System.Diagnostics;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Middleware;

/// <summary>
/// Marks an endpoint of the BFF's own that exists only on the public demo. While
/// <c>Demo:Enabled</c> is false, <see cref="DemoModeMiddleware"/> reads it from the endpoint
/// metadata and answers 404, as for a path the BFF does not have.
/// </summary>
/// <remarks>
/// One endpoint carries it, the claim. The API has a marker of the same name on its own claim
/// (<c>AzureBank.Api.Attributes.DemoOnlyAttribute</c>); the two hosts share no web project.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DemoOnlyAttribute : Attribute
{
}

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
/// Answers 404 to an endpoint that exists only on the public demo
/// (<see cref="DemoOnlyAttribute"/>) while <c>Demo:Enabled</c> is false, and refuses with 403 an
/// endpoint the demo closes (<see cref="ClosedInDemoAttribute"/>) while it is true.
/// </summary>
/// <remarks>
/// <para>
/// <b>404, as a path the BFF does not have.</b> A deployment that is not the demo should not
/// answer for the demo's door: the status is set and nothing is written, which is what a POST to
/// a path with no route gets here. The two answers have the same status, the same headers by
/// name and no body (<c>DemoClaimTests.WithTheFlagOff_TheClaimIs404_...</c> compares them).
/// </para>
/// <para>
/// <b>403, in the API's own words.</b> The API refuses a registration on the demo with 403, the
/// code <c>REGISTRATION_CLOSED</c> and one sentence. The BFF answers the same code and the same
/// sentence without asking it: the body is written here, in the shape the API's refusals have
/// (<c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c>, <c>instance</c>, <c>errorCode</c>,
/// <c>traceId</c>), as <see cref="FetchMetadataMiddleware"/> writes its own 403. <c>instance</c>
/// is the path the browser asked for.
/// </para>
/// <para>
/// <b>Before model binding.</b> A marker is read from the endpoint's metadata, so neither answer
/// depends on the body: a malformed body is not answered 400, nor a form post 415, each of which
/// would tell the caller what a request should look like on a deployment that takes none.
/// </para>
/// <para>
/// <b>After the rate limiter.</b> A closed door is still a door somebody can hammer: a request to
/// it spends the <c>auth</c> policy's allowance like any other, and past the limit the answer is
/// the limiter's 429. That holds for the claim with the demo off too, where a path the BFF does
/// not have would still be 404 (<c>DemoClaimTests.WithTheFlagOff_TheClaimsPath_...</c>).
/// </para>
/// <para>
/// <b>The endpoint is hidden or closed, not its path.</b> Another method on the path never
/// reaches the endpoint, so no marker is read: the answer is 405 with <c>Allow</c> naming the
/// endpoint's method, with the demo off or on, as the path of sign-in answers
/// (<c>DemoClaimTests.AnotherMethodOnTheClaimsPath_...</c> and
/// <c>AnotherMethodOnRegistrationsPath_...</c> pin it, for six methods each). A path with no
/// route is 404 under each of them, so the 405 and the 429 above tell that this build has the
/// claim. Neither tells whether the demo is on.
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
        if (!_demoEnabled && metadata?.GetMetadata<DemoOnlyAttribute>() is not null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

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
    /// Hides the demo's own endpoints while the demo is off, and refuses the endpoints the demo
    /// closes while it is on. After the rate limiter, before the controllers bind anything.
    /// </summary>
    public static IApplicationBuilder UseDemoMode(this IApplicationBuilder app) =>
        app.UseMiddleware<DemoModeMiddleware>();
}
