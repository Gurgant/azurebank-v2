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
/// The connection string is checked at STARTUP. Until 2026-09-25 nothing checked it: measured, both
/// hosts as Production in containers, the API started without one and the first sign-in answered 500.
/// </summary>
public class DatabaseOptionsTests
{
    private static ServiceProvider Root(string? connectionString)
    {
        var values = new Dictionary<string, string?>();
        if (connectionString is not null)
        {
            values["ConnectionStrings:DefaultConnection"] = connectionString;
        }

        var services = new ServiceCollection();
        services.AddDatabaseOptions(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Conect Timeout=30")] // a mistyped keyword
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Database")] // not key=value
    public void AMissingOrUnparseableConnectionString_StopsTheHostAtStart(string? connectionString)
    {
        using var root = Root(connectionString);

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("ConnectionStrings:DefaultConnection must be configured");
    }

    [Fact]
    public void AConnectionStringThatParses_Starts()
    {
        using var root = Root(CustomWebApplicationFactory.PlaceholderConnectionString);

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void ThroughTheRealRoot_NoConnectionString_IsRefused_ByTheValidatorTheHostRunsAtStart()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Idempotency:HashKey"] = CustomWebApplicationFactory.IdempotencyHashKey,
            ["StepUp:BindingKey"] = CustomWebApplicationFactory.StepUpBindingKey,
            ["ServiceCredential:BffKey"] = CustomWebApplicationFactory.ServiceCredentialKey,
            ["Security:PinPepper"] = CustomWebApplicationFactory.PinPepper,
            ["Audit:ChainKey"] = CustomWebApplicationFactory.AuditChainKey,
            ["Audit:AnchorKey"] = CustomWebApplicationFactory.AuditAnchorKey,
            ["Jwt:Secret"] = CustomWebApplicationFactory.JwtSecret,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices(configuration);
        using var root = services.BuildServiceProvider();

        var act = () => root.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("ConnectionStrings:DefaultConnection must be configured");
    }
}
