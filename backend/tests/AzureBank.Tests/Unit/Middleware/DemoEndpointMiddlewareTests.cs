using AzureBank.Api.Attributes;
using AzureBank.Api.Middleware;
using AzureBank.Shared.Options;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureBank.Tests.Unit.Middleware;

/// <summary>
/// The middleware that keeps the public demo's own endpoints out of a deployment that is not the
/// demo: with <c>Demo:Enabled</c> false, an endpoint marked <see cref="DemoOnlyAttribute"/> is 404
/// and nothing behind the middleware runs; everything else passes.
/// </summary>
/// <remarks>
/// On a bare <see cref="DefaultHttpContext"/>, so each of the two conditions is shown alone: the
/// flag, and the marker. What the 404 looks like once the status-code pages have filled it, and
/// that it comes before model binding, is shown through the host (<c>DemoModeEndpointTests</c>).
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

    // CONTROL: green before this change. It is what fails if the middleware refuses a demo-only
    // endpoint whatever the flag says.
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

    // CONTROL: green before this change. It is what fails if the middleware refuses every endpoint
    // while the demo is off, and not only the marked ones.
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

    // CONTROL: green before this change. A path that matched no route has no endpoint to read a
    // marker from, and must reach the rest of the pipeline to be answered as one.
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
}
