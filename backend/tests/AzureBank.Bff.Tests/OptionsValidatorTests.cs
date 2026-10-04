using System.Globalization;
using AzureBank.Bff.Options;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The rate limiter builds its window options lazily inside the partition factory, so a
/// non-positive value would only surface as a per-request failure. These pin the
/// fail-at-startup behaviour instead (ADR-0013).
/// </summary>
public class RateLimitingOptionsValidatorTests
{
    private readonly RateLimitingOptionsValidator _sut = new();

    [Fact]
    public void Validate_Defaults_Succeeds()
    {
        _sut.Validate(null, new RateLimitingOptions()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveGlobalPermitLimit_Fails(int value)
    {
        var result = _sut.Validate(null, new RateLimitingOptions { GlobalPermitLimit = value });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("GlobalPermitLimit");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Validate_NonPositiveGlobalWindow_Fails(int value)
    {
        var result = _sut.Validate(null, new RateLimitingOptions { GlobalWindowSeconds = value });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("GlobalWindowSeconds");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveAuthPermitLimit_Fails(int value)
    {
        var result = _sut.Validate(null, new RateLimitingOptions { AuthPermitLimit = value });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("AuthPermitLimit");
    }

    [Fact]
    public void Validate_NonPositiveAuthWindow_Fails()
    {
        var result = _sut.Validate(null, new RateLimitingOptions { AuthWindowSeconds = 0 });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("AuthWindowSeconds");
    }

    [Fact]
    public void Validate_NonPositiveAuthSegmentsPerWindow_Fails()
    {
        // 0 would throw when the sliding window is built; 1 silently degrades to a fixed
        // window and hands back the 2x boundary burst.
        var result = _sut.Validate(null, new RateLimitingOptions { AuthSegmentsPerWindow = 0 });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("AuthSegmentsPerWindow");
    }

    [Fact]
    public void Validate_NonPositiveLookupPermitLimit_Fails()
    {
        var result = _sut.Validate(null, new RateLimitingOptions { LookupPermitLimit = 0 });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("LookupPermitLimit");
    }
}

/// <summary>
/// A typo'd proxy IP would otherwise be skipped silently, leaving X-Forwarded-For untrusted
/// and collapsing every client into one rate-limit partition — an invisible failure of a
/// security control. These pin the refuse-to-start behaviour (ADR-0013).
/// </summary>
public class ProxyOptionsValidatorTests
{
    private readonly ProxyOptionsValidator _sut = new();

    [Fact]
    public void Validate_NoProxies_Succeeds()
    {
        // The default: the BFF is the edge, X-Forwarded-For is not honoured.
        _sut.Validate(null, new ProxyOptions()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ValidProxyIps_Succeeds()
    {
        var options = new ProxyOptions { KnownProxies = ["10.0.0.1", "::1"] };

        _sut.Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_UnparseableProxyIp_Fails()
    {
        var options = new ProxyOptions { KnownProxies = ["10.0.0.1", "not-an-ip"] };

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("not-an-ip");
    }

    [Fact]
    public void Validate_NullKnownProxies_Fails()
    {
        var result = _sut.Validate(null, new ProxyOptions { KnownProxies = null! });

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_NonPositiveForwardLimit_WithProxies_Fails()
    {
        var options = new ProxyOptions { KnownProxies = ["10.0.0.1"], ForwardLimit = 0 };

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ForwardLimit");
    }
}

/// <summary>
/// The cookie name feeds every session read and the timeouts ARE the session-lifetime
/// control; a config that nulls the name would otherwise surface as per-request failures
/// (or an NRE inside the __Host- PostConfigure). Pin the fail-at-startup behaviour
/// instead (ADR-0018).
/// </summary>
public class BffSessionOptionsValidatorTests
{
    private readonly BffSessionOptionsValidator _sut = new();

    [Fact]
    public void Validate_Defaults_Succeeds()
    {
        _sut.Validate(null, new BffSessionOptions()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingCookieName_Fails(string? cookieName)
    {
        var result = _sut.Validate(null, new BffSessionOptions { CookieName = cookieName! });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("CookieName");
    }

    [Fact]
    public void Validate_NonPositiveInactivityTimeout_Fails()
    {
        var result = _sut.Validate(null, new BffSessionOptions { InactivityTimeoutMinutes = 0 });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("InactivityTimeoutMinutes");
    }

    [Fact]
    public void Validate_NonPositiveAbsoluteTimeout_Fails()
    {
        var result = _sut.Validate(null, new BffSessionOptions { AbsoluteTimeoutMinutes = -1 });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("AbsoluteTimeoutMinutes");
    }
}

/// <summary>
/// The validator alone is not the whole control — the __Host- PostConfigure runs BEFORE
/// it, and a prefix applied to a whitespace-only name would turn it non-whitespace and
/// slip past validation. This pins the PIPELINE: a whitespace CookieName must stop the
/// host from starting, PostConfigure notwithstanding.
/// </summary>
public class SessionOptionsPipelineTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SessionOptionsPipelineTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public void WhitespaceCookieName_FailsStartup_DespiteTheHostPrefixPostConfigure()
    {
        // Production: the environment where the __Host- PostConfigure is active — the
        // exact interplay that could mask the malformed value.
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Session:CookieName", "   ");
        });

        var exception = Record.Exception(() => factory.CreateClient());

        exception.Should().NotBeNull(
            "a whitespace-only cookie name must fail ValidateOnStart, not boot a BFF " +
            "whose every session read is silently broken");
    }
}

/// <summary>
/// <c>BackendApi:TimeoutSeconds</c> becomes the BFF's own client's <c>HttpClient.Timeout</c>, which
/// takes at most <c>int.MaxValue</c> milliseconds. A value past that must stop the host at startup:
/// with a lower bound alone it started, and every <c>CreateClient("BackendApi")</c> threw afterwards.
/// </summary>
public class BackendApiTimeoutPipelineTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public BackendApiTimeoutPipelineTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Theory]
    [InlineData(0)]
    [InlineData(BackendApiOptions.MaxTimeoutSeconds + 1)] // one second past what HttpClient takes
    public void AnUnusableTimeout_FailsStartup(int seconds)
    {
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("BackendApi:TimeoutSeconds", seconds.ToString(CultureInfo.InvariantCulture)));

        var exception = Record.Exception(() => factory.CreateClient());

        exception.Should().NotBeNull("a timeout HttpClient cannot take must fail ValidateOnStart");
    }

    [Fact]
    public void TheBffWaits55Seconds_ByDefaultAndAsShipped()
    {
        // Above the API's 40-second request deadline plus its cancellation and its release, so every
        // answer the API gives before a commit reaches the visitor (ADR-0058, TimeoutChainTests); and
        // it drives YARP's activity timeout as well as this client's.
        new BackendApiOptions().TimeoutSeconds.Should().Be(55, "the code default");

        var shipped = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackendApiOptions>>().Value;
        shipped.TimeoutSeconds.Should().Be(55, "the value appsettings.json ships");

        _factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("BackendApi")
            .Timeout.Should().Be(TimeSpan.FromSeconds(55));
    }

    [Fact]
    public void TheLongestTimeoutHttpClientTakes_Starts_AndTheClientIsBuilt()
    {
        // The control for the row above: one second less is accepted, and the client is built with it.
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting(
                "BackendApi:TimeoutSeconds",
                BackendApiOptions.MaxTimeoutSeconds.ToString(CultureInfo.InvariantCulture)));
        factory.CreateClient();

        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("BackendApi");

        client.Timeout.Should().Be(TimeSpan.FromSeconds(BackendApiOptions.MaxTimeoutSeconds));
    }
}

/// <summary>
/// The BFF binds the public demo's settings and checks them at startup, as the API does: a value
/// out of range stops the host whether or not the demo is on. It has no rule for the client-key
/// secret, which is the API's, so it starts with the demo on and no secret.
/// </summary>
public class DemoOptionsPipelineTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DemoOptionsPipelineTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static DemoOptions DemoOf(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptions<DemoOptions>>().Value;

    [Fact]
    public void ADemoValueOutOfRange_StopsTheBff()
    {
        // 0 is one below the floor of Demo:Pool:TargetFree, a number the BFF never reads.
        var outOfRange = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Demo:Pool:TargetFree", "0"));

        var exception = Record.Exception(() => outOfRange.CreateClient());

        exception.Should().NotBeNull(
            "a demo setting out of range must fail ValidateOnStart in every host that reads the section");

        // Any failure to start would satisfy the line above. So the same setting with a value in
        // its range: that host starts, and what stopped the other one is the value.
        var inRange = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Demo:Pool:TargetFree", "20"));
        inRange.CreateClient();
        DemoOf(inRange).Pool.TargetFree.Should().Be(20);
    }

    [Fact]
    public void WithTheFlagOn_TheBffStartsWithoutAClientKeySecret()
    {
        var factory = _factory.WithWebHostBuilder(builder => builder.UseSetting("Demo:Enabled", "true"));
        factory.CreateClient();

        var demo = DemoOf(factory);

        demo.Enabled.Should().BeTrue("the BFF binds the section, so the flag a deployment sets reaches it");
        demo.ClientKeySecret.Should().BeNull();
    }

    // CONTROL: green before this change. Run with Demo__Enabled=true in the environment, a host
    // no test asked the demo of has it on: this is the test that then fails by name.
    [Fact]
    public void TheDefaultBffHost_HasTheDemoOff()
    {
        DemoOf(_factory).Enabled.Should().BeFalse(
            "no test asked for the demo, and no committed settings file sets it");
    }
}
