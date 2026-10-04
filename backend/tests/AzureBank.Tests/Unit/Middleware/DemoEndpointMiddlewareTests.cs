using AzureBank.Api.Attributes;
using AzureBank.Api.Middleware;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureBank.Tests.Unit.Middleware;

/// <summary>
/// The middleware that keeps the public demo's own endpoints out of a deployment that is not the
/// demo, and closes on the demo the endpoints it has no use for: with <c>Demo:Enabled</c> false, an
/// endpoint marked <see cref="DemoOnlyAttribute"/> is 404; with it true, one marked
/// <see cref="ClosedInDemoAttribute"/> is refused as closed. Nothing behind the middleware runs
/// for either, and everything else passes.
/// </summary>
/// <remarks>
/// On a bare <see cref="DefaultHttpContext"/>, so each of the two conditions is shown alone: the
/// flag, and the marker. What the 404 looks like once the status-code pages have filled it, what
/// the refusal looks like once the exception handler has written it, and that both come before
/// model binding, is shown through the host (<c>DemoModeEndpointTests</c>).
/// </remarks>
public class DemoEndpointMiddlewareTests
{
    private static Endpoint EndpointWith(params object[] metadata) =>
        new(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "an endpoint");

    /// <summary>Runs one request through the middleware and says whether the next one ran.</summary>
    private static async Task<(HttpContext Context, bool NextRan)> SendAsync(bool demoEnabled, Endpoint? endpoint)
    {
        var nextRan = false;
        var middleware = new DemoEndpointMiddleware(
            _ =>
            {
                nextRan = true;
                return Task.CompletedTask;
            },
            Options.Create(new DemoOptions { Enabled = demoEnabled }));

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(endpoint);

        await middleware.InvokeAsync(context);
        return (context, nextRan);
    }

    [Fact]
    public async Task WithTheDemoOff_ADemoOnlyEndpointIs404_AndNothingBehindItRuns()
    {
        var (context, nextRan) = await SendAsync(demoEnabled: false, EndpointWith(new DemoOnlyAttribute()));

        using (new AssertionScope())
        {
            nextRan.Should().BeFalse("with the demo off the endpoint is not there: no binding, no action");
            context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
            context.Response.Body.Length.Should().Be(
                0, "the response is left empty for the status-code pages to fill, as they do for a path that matches no route");
            context.Response.ContentType.Should().BeNull();
        }
    }

    // CONTROL: green on a middleware that does nothing. It is what fails if the middleware refuses
    // a demo-only endpoint whatever the flag says.
    [Fact]
    public async Task WithTheDemoOn_ADemoOnlyEndpointRuns()
    {
        var (context, nextRan) = await SendAsync(demoEnabled: true, EndpointWith(new DemoOnlyAttribute()));

        using (new AssertionScope())
        {
            nextRan.Should().BeTrue();
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK, "the middleware leaves the response alone");
        }
    }

    // CONTROL: green on a middleware that does nothing. It is what fails if the middleware refuses
    // every endpoint while the demo is off, and not only the marked ones.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEndpointThatIsNotDemoOnly_RunsWhateverTheFlag(bool demoEnabled)
    {
        var (context, nextRan) = await SendAsync(demoEnabled, EndpointWith(new TokenEndpointAttribute()));

        using (new AssertionScope())
        {
            nextRan.Should().BeTrue("the marker, not the flag alone, is what closes an endpoint");
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }
    }

    // CONTROL: green on a middleware that does nothing. A path that matched no route has no
    // endpoint to read a marker from, and must reach the rest of the pipeline to be answered as
    // one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARequestThatMatchedNoEndpoint_RunsWhateverTheFlag(bool demoEnabled)
    {
        var (context, nextRan) = await SendAsync(demoEnabled, endpoint: null);

        using (new AssertionScope())
        {
            nextRan.Should().BeTrue();
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }
    }

    // ── An endpoint the demo closes ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithTheDemoOn_AnEndpointClosedInDemo_IsRefusedAsRegistrationClosed_AndNothingBehindItRuns()
    {
        var nextRan = false;
        var middleware = new DemoEndpointMiddleware(
            _ =>
            {
                nextRan = true;
                return Task.CompletedTask;
            },
            Options.Create(new DemoOptions { Enabled = true }));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(EndpointWith(new ClosedInDemoAttribute()));

        var thrown = await Record.ExceptionAsync(() => middleware.InvokeAsync(context));

        using (new AssertionScope())
        {
            // An exception, not a response: the exception handler above the middleware writes the
            // 403, with the code, the sentence and the trace id every refusal of the API carries.
            thrown.Should().BeOfType<RegistrationClosedException>("on the demo the endpoint is closed, whatever the request carries");
            ((thrown as AppException)?.StatusCode).Should().Be(StatusCodes.Status403Forbidden);
            ((thrown as AppException)?.ErrorCode).Should().Be(ErrorCodes.RegistrationClosed);
            (thrown?.Message).Should().Be(RegistrationClosedException.Detail);
            nextRan.Should().BeFalse("no binding, no validation, no action");
            context.Response.Body.Length.Should().Be(0, "the middleware writes nothing itself");
        }
    }

    // CONTROL: green on a middleware that does nothing. It is what fails if the middleware closes
    // the endpoint on a deployment that is not the demo, where registration is open.
    [Fact]
    public async Task WithTheDemoOff_AnEndpointClosedInDemo_Runs()
    {
        var (context, nextRan) = await SendAsync(demoEnabled: false, EndpointWith(new ClosedInDemoAttribute()));

        using (new AssertionScope())
        {
            nextRan.Should().BeTrue("the marker closes an endpoint on the demo only");
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }
    }
}
