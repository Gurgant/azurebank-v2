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

    [Theory]
    [InlineData("ftp://collector.example.com:4318")]
    [InlineData("file:///var/otlp")]
    [InlineData("ws://collector.example.com:4318")]
    public void UnsupportedScheme_FailsClosedInProduction(string endpoint)
    {
        // ALLOWLIST semantics: only https and loopback http pass. Anything else — even a
        // scheme nobody would deliberately configure — is a config error, not a pass.
        var act = () => OtlpEndpointGuard.EnsureSecureExportEndpoint(endpoint, isDevelopment: false, "Production");

        act.Should().Throw<InvalidOperationException>().WithMessage("*unsupported scheme*");
    }

    [Fact]
    public void ExceptionMessages_NeverEchoCredentials()
    {
        // Startup errors are logged (and exported): a secret-bearing endpoint must be redacted
        // to scheme://host:port in the message. Assert the SHAPE (no user-info, no path, no
        // query markers), not just the sample secret values — a regression that echoed
        // "/v1?token=..." while stripping only the known samples must fail here.
        var act = () => OtlpEndpointGuard.EnsureSecureExportEndpoint(
            "http://admin:hunter2@collector.example.com:4318/v1?token=tok123",
            isDevelopment: false, "Production");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .NotContain("hunter2").And.NotContain("admin").And.NotContain("tok123")
            .And.NotContain("/v1").And.NotContain("token=").And.NotContain("?")
            .And.NotContain("@")
            .And.Contain("http://collector.example.com:4318");
    }

    [Fact]
    public void UnparseableEndpoint_MessageNeverEchoesTheRawValue()
    {
        // We cannot redact what we cannot parse — so the raw value is not echoed at all.
        // BOTH assertions on purpose: NotContain(whole raw value) alone would still pass if
        // the message echoed only a fragment, so the distinctive fragment is asserted too.
        const string endpoint = "::secret-blob-pasted-by-mistake::";

        var act = () => OtlpEndpointGuard.EnsureSecureExportEndpoint(
            endpoint, isDevelopment: false, "Production");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().NotContain(endpoint).And.NotContain("secret-blob");
    }
}
