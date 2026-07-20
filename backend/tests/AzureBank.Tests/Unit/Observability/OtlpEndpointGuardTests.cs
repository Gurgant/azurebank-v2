using AzureBank.Shared.Observability;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Observability;

/// <summary>
/// Pins the single OTLP-endpoint security policy shared by the API and the BFF: outside
/// Development, cleartext-to-remote and unparseable endpoints must fail fast (fail-closed);
/// loopback http, https, empty (export off) and anything in Development stay allowed.
/// </summary>
public class OtlpEndpointGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyEndpoint_IsAllowed_ExportIsSimplyOff(string? endpoint)
    {
        var act = () => OtlpEndpointGuard.EnsureSecureExportEndpoint(endpoint, isDevelopment: false, "Production");

        act.Should().NotThrow();
    }

    [Fact]
    public void Development_BypassesTheGuard()
    {
        var act = () => OtlpEndpointGuard.EnsureSecureExportEndpoint(
            "http://collector.example.com:4318", isDevelopment: true, "Development");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("http://127.0.0.1:4318")]
    [InlineData("http://localhost:4318")]
    [InlineData("https://otlp.example.com:4318")]
    public void LoopbackHttp_AndAnyHttps_AreAllowedInProduction(string endpoint)
    {
        var act = () => OtlpEndpointGuard.EnsureSecureExportEndpoint(endpoint, isDevelopment: false, "Production");

        act.Should().NotThrow();
    }

    [Fact]
    public void CleartextToRemoteHost_FailsFastInProduction()
    {
        var act = () => OtlpEndpointGuard.EnsureSecureExportEndpoint(
            "http://collector.example.com:4318", isDevelopment: false, "Production");

        act.Should().Throw<InvalidOperationException>().WithMessage("*cleartext http*");
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("4318")]
    public void UnparseableEndpoint_FailsClosedInProduction(string endpoint)
    {
        // The old inline guard silently passed anything Uri.TryCreate rejected — exporting
        // to an unverifiable destination. Fail-closed is the contract now.
        var act = () => OtlpEndpointGuard.EnsureSecureExportEndpoint(endpoint, isDevelopment: false, "Production");

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a valid absolute URI*");
    }
}
