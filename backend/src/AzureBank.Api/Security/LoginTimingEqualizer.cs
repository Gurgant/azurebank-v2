using AzureBank.Shared.Entities;
using Microsoft.AspNetCore.Identity;

namespace AzureBank.Api.Security;

/// <inheritdoc />
public sealed class LoginTimingEqualizer : ILoginTimingEqualizer
{
    // PasswordHasher ignores the user argument; a throwaway instance is fine.
    private static readonly ApplicationUser DummyUser =
        new() { AzureTag = string.Empty, FirstName = string.Empty, LastName = string.Empty };

    private readonly IPasswordHasher<ApplicationUser> _hasher;
    private readonly string _dummyHash;

    public LoginTimingEqualizer(IPasswordHasher<ApplicationUser> hasher)
    {
        // The app's registered IPasswordHasher<ApplicationUser> (the same one
        // UserManager.CheckPasswordAsync uses, handed in by the DI factory), so the
        // equalizing cost tracks it exactly for any hasher type or tuned options, with no
        // drift. This type is a singleton, so the (expensive) reference hash below is
        // computed once at startup — an unknown-email login then does a single
        // verification, never a hash + verify.
        _hasher = hasher;
        _dummyHash = _hasher.HashPassword(DummyUser, "login-timing-equalization-dummy");
    }

    /// <inheritdoc />
    public void SpendVerifyCost(string password)
        => _hasher.VerifyHashedPassword(DummyUser, _dummyHash, password);
}
