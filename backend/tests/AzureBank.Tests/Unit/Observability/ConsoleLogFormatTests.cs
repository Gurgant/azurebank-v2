using AzureBank.Shared.Observability;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Unit.Observability;

/// <summary>
/// JSON on the console in Production and nowhere else, decided the same way before the host exists
/// as after (<see cref="ConsoleLogFormat"/>).
/// </summary>
public class ConsoleLogFormatTests
{
    [Theory]
    [InlineData("Production", true)]
    [InlineData("production", true)] // IHostEnvironment.IsProduction() ignores case too
    [InlineData("Development", false)]
    [InlineData("Testing", false)] // the API test factory's, and FailedStartupExitCodeTests'
    [InlineData("Staging", false)]
    public void IsJson_OnlyInProduction(string environment, bool json) =>
        ConsoleLogFormat.IsJson(environment).Should().Be(json);

    [Theory]
    [InlineData(null, null, true)] // neither set: the host runs as Production
    [InlineData(null, "Production", true)]
    [InlineData(null, "Development", false)]
    [InlineData("Development", "Production", false)] // DOTNET_ENVIRONMENT decides, as the host's did
    [InlineData("Production", "Development", true)]
    [InlineData("", "Testing", false)] // an empty variable is not a value
    public void IsJsonBeforeTheHost_ReadsTheVariablesInTheHostsOrder(string? dotnet, string? aspnetcore, bool json)
    {
        var variables = new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = dotnet,
            ["ASPNETCORE_ENVIRONMENT"] = aspnetcore,
        };

        ConsoleLogFormat.IsJsonBeforeTheHost(name => variables.GetValueOrDefault(name)).Should().Be(json);
    }
}
