using AzureBank.Shared.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Security;

/// <inheritdoc />
public sealed class LoginTimingEqualizer : ILoginTimingEqualizer
{
    // PasswordHasher ignores the user argument; a throwaway instance is fine.
    private static readonly ApplicationUser DummyUser =
        new() { AzureTag = string.Empty, FirstName = string.Empty, LastName = string.Empty };

    private readonly IPasswordHasher<ApplicationUser> _hasher;
    private readonly string _dummyHash;

    public LoginTimingEqualizer(IOptions<PasswordHasherOptions> passwordHasherOptions)
    {
        // Build a hasher from the SAME options ASP.NET Identity's IPasswordHasher uses,
        // so the equalizing cost tracks UserManager.CheckPasswordAsync (no drift if the
        // iteration count is ever tuned). Because this type is a singleton, the expensive
        // reference hash below is computed exactly ONCE for the app lifetime — an
        // unknown-email login then does a single verification, never a hash + verify.
        _hasher = new PasswordHasher<ApplicationUser>(passwordHasherOptions);
        _dummyHash = _hasher.HashPassword(DummyUser, "login-timing-equalization-dummy");
    }

    /// <inheritdoc />
    public void SpendVerifyCost(string password)
        => _hasher.VerifyHashedPassword(DummyUser, _dummyHash, password);
}
