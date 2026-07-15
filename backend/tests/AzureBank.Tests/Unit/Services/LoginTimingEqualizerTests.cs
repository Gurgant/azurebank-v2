using AzureBank.Api.Security;
using AzureBank.Shared.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AzureBank.Tests.Unit.Services;

/// <summary>
/// Unit tests for the login-timing equalizer (ADR-0012): it performs one real password
/// verification against a fixed dummy hash so an unknown-email login costs the same as
/// a real one. Registered as a singleton, so it must be immutable/thread-safe.
/// </summary>
public class LoginTimingEqualizerTests
{
    private readonly LoginTimingEqualizer _sut =
        new(new PasswordHasher<ApplicationUser>(Options.Create(new PasswordHasherOptions())));

    [Fact]
    public void SpendVerifyCost_RunsWithoutThrowing()
    {
        // Performs a real PBKDF2 verification against a fixed dummy hash; the boolean
        // result is intentionally discarded — its only purpose is to spend equal CPU time.
        var act = () => _sut.SpendVerifyCost("any-password");

        act.Should().NotThrow();
    }

    [Fact]
    public void SpendVerifyCost_IsSafeToCallConcurrently()
    {
        // A singleton is shared across all concurrent login requests.
        var act = () => Parallel.For(0, 16, _ => _sut.SpendVerifyCost("pw"));

        act.Should().NotThrow();
    }
}
