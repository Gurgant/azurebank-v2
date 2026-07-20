using AzureBank.Bff.Options;
using FluentAssertions;

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
