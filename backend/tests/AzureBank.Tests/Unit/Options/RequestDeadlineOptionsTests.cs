using AzureBank.Api.Extensions;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// The folder's existing namespace: a namespace literally named "Options" under Unit would shadow
// `Options.Create(...)` in every sibling test file.
namespace AzureBank.Tests.Unit.Configuration;

/// <summary>
/// The request deadline (ADR-0058) is validated at STARTUP, not on the first slow request, and so is
/// the idempotency claim's stale age that must outlast it (ADR-0009).
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

    // ── The idempotency claim's stale age, which the deadline bounds ─────────────────────────────

    [Theory]
    [InlineData(null, null, "00:02:00")] // as shipped: 2 minutes, which appsettings.json repeats, over 40 s
    [InlineData("00:01:40", null, "00:01:40")] // the deadline and exactly a minute
    [InlineData("00:02:30", "90", "00:02:30")]
    public void AStaleAgeAMinutePastTheDeadline_PassesStartupValidation_AndBinds(
        string? staleAfter, string? deadlineSeconds, string expected)
    {
        using var root = ApiRoot(staleAfter, deadlineSeconds);

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().NotThrow();
        root.GetRequiredService<IOptions<IdempotencyOptions>>().Value.ProcessingStaleAfter
            .Should().Be(TimeSpan.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("00:01:39", null)] // a second short of the deadline and a minute
    [InlineData("00:02:00", "61")] // the shipped age under a longer deadline
    [InlineData("00:10:00", "600")]
    public void AStaleAgeTheRequestCanStillBeRunningAt_StopsTheHostAtStart(string staleAfter, string? deadlineSeconds)
    {
        // A claim taken over while its request can still commit: the fence stops a second
        // execution, but the request that was about to succeed fails instead.
        using var root = ApiRoot(staleAfter, deadlineSeconds);

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain(
            "Idempotency:ProcessingStaleAfter must be at least one minute longer than RequestDeadline:Seconds");
    }

    /// <summary>
    /// The API's own registrations (<c>AddApplicationServices</c>, which also adds the request
    /// deadline), with every secret the root validates at start, so the one failure left is the rule
    /// under test.
    /// </summary>
    private static ServiceProvider ApiRoot(string? staleAfter, string? deadlineSeconds)
    {
        var values = new Dictionary<string, string?>
        {
            ["Idempotency:HashKey"] = CustomWebApplicationFactory.IdempotencyHashKey,
            ["StepUp:BindingKey"] = CustomWebApplicationFactory.StepUpBindingKey,
            ["ServiceCredential:BffKey"] = CustomWebApplicationFactory.ServiceCredentialKey,
            ["Security:PinPepper"] = CustomWebApplicationFactory.PinPepper,
            ["Audit:ChainKey"] = CustomWebApplicationFactory.AuditChainKey,
            ["Audit:AnchorKey"] = CustomWebApplicationFactory.AuditAnchorKey,
            ["Jwt:Secret"] = CustomWebApplicationFactory.JwtSecret,
            ["ConnectionStrings:DefaultConnection"] = CustomWebApplicationFactory.PlaceholderConnectionString,
        };

        // Absent, not null: a key present with no value binds as 0.
        if (staleAfter is not null)
        {
            values["Idempotency:ProcessingStaleAfter"] = staleAfter;
        }

        if (deadlineSeconds is not null)
        {
            values["RequestDeadline:Seconds"] = deadlineSeconds;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices(configuration);
        return services.BuildServiceProvider();
    }
}
