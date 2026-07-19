using AzureBank.Api.Extensions;
using AzureBank.Api.Observability;
using FluentAssertions;
using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace AzureBank.Tests.Unit.Observability;

/// <summary>
/// Proves the redaction DI wiring end-to-end through the REAL registration
/// (<see cref="ObservabilityServiceCollectionExtensions.AddObservability"/>): a consumer
/// asking <see cref="IRedactorProvider"/> for the AzureBank/PII classification gets the
/// email masker — the exact resolution path AuthService's constructor performs.
/// </summary>
public class RedactionRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        // Development so the extension's prod-TLS guard can't trip on a developer
        // machine that happens to have OTEL_EXPORTER_OTLP_ENDPOINT set.
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);

        var services = new ServiceCollection();
        services.AddObservability(new ConfigurationBuilder().Build(), environment.Object);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void GetRedactor_ForPiiClassification_ResolvesEmailMaskingRedactor()
    {
        using var provider = BuildProvider();

        var redactor = provider.GetRequiredService<IRedactorProvider>()
            .GetRedactor(new DataClassificationSet(DataClassifications.Pii));

        redactor.Should().BeOfType<EmailMaskingRedactor>();
        // And the resolved instance really masks — the full loop a call site relies on.
        redactor.Redact("john@example.com").Should().Be("j***@example.com");
    }

    [Fact]
    public void GetRedactor_ForUnclassifiedData_DoesNotGetTheEmailMasker()
    {
        using var provider = BuildProvider();

        // A classification we never registered must fall back to the framework default
        // (erasure), not accidentally inherit the email-shaped masking.
        var redactor = provider.GetRequiredService<IRedactorProvider>()
            .GetRedactor(new DataClassificationSet(new DataClassification("AzureBank", "Unregistered")));

        redactor.Should().NotBeOfType<EmailMaskingRedactor>();
    }
}
