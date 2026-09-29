using AzureBank.Api.Extensions;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// The folder's existing namespace: a namespace literally named "Options" under Unit would shadow
// `Options.Create(...)` in every sibling test file.
namespace AzureBank.Tests.Unit.Configuration;

/// <summary>
/// The request deadline (ADR-0058) is validated at STARTUP, not on the first slow request.
/// </summary>
/// <remarks>
/// Driven through <see cref="IStartupValidator"/>, which is what <c>ValidateOnStart()</c> registers
/// and what the host runs before it listens, so these prove the registration in
/// <c>AddRequestDeadline</c> and not only the attribute. Without <c>ValidateDataAnnotations</c> the
/// <c>[Range]</c> is decoration: a 0 would bind, and every request would answer 503 at once.
/// </remarks>
public class RequestDeadlineOptionsTests
{
    private static ServiceProvider Root(string? seconds)
    {
        var values = new Dictionary<string, string?>();
        if (seconds is not null)
        {
            values["RequestDeadline:Seconds"] = seconds;
        }

        var services = new ServiceCollection();
        services.AddRequestDeadline(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("0")]   // every request would be cancelled as it starts
    [InlineData("-1")]
    [InlineData("601")] // past any caller's patience: the BFF gives up long before
    public void ADeadlineOutOfRange_StopsTheHostAtStart(string seconds)
    {
        using var root = Root(seconds);

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("RequestDeadline:Seconds must be between 1 and 600.");
    }

    [Theory]
    [InlineData(null, 40)] // no section: the shipped default, which appsettings.json repeats
    [InlineData("1", 1)]
    [InlineData("600", 600)]
    public void ADeadlineInRange_PassesStartupValidation_AndBinds(string? seconds, int expected)
    {
        using var root = Root(seconds);

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().NotThrow();
        root.GetRequiredService<IOptions<RequestDeadlineOptions>>().Value.Seconds.Should().Be(expected);
    }
}
