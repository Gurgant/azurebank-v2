using AzureBank.Api.Observability;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Serilog.Events;
using Xunit;

namespace AzureBank.Tests.Unit.Observability;

/// <summary>
/// The request line's level (<see cref="RequestLogRoute.LevelFor"/>): Serilog's default, except a
/// passing <c>/health</c> probe, which is written at no configured level.
/// </summary>
public class RequestLogRouteLevelTests
{
    [Theory]
    [InlineData("/health/live", 200, false, LogEventLevel.Verbose)] // below every floor: not written
    [InlineData("/health/ready", 200, false, LogEventLevel.Verbose)]
    [InlineData("/health/ready", 503, false, LogEventLevel.Error)] // a failing probe still speaks
    [InlineData("/health/live", 200, true, LogEventLevel.Error)]
    [InlineData("/healthz", 200, false, LogEventLevel.Information)] // a path segment, not a prefix
    [InlineData("/api/accounts", 401, false, LogEventLevel.Information)]
    [InlineData("/api/accounts", 500, false, LogEventLevel.Error)]
    public void LevelFor_IsSerilogsRule_ButAPassingProbeIsVerbose(string path, int status, bool threw, LogEventLevel level)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.StatusCode = status;

        RequestLogRoute.LevelFor(context, 1.0, threw ? new InvalidOperationException() : null).Should().Be(level);
    }
}
