using AzureBank.Shared.Entities;
using Microsoft.AspNetCore.Identity;

namespace AzureBank.Api.Security;

/// <inheritdoc />
public sealed class LoginTimingEqualizer : ILoginTimingEqualizer
{
    // PasswordHasher ignores the user argument; a throwaway instance is fine.
    private static readonly ApplicationUser DummyUser =
        new() { AzureTag = string.Empty, FirstName = string.Empty, LastName = string.Empty };

    // Process-wide, write-once cache of the reference hash. Static (not an instance field)
    // so it is computed exactly ONCE on the first unknown-email login and reused forever —
    // never per request. Kept here rather than in a singleton so this service can stay
    // scoped and take the request's own IPasswordHasher<ApplicationUser> by normal
    // constructor injection: no captive dependency, and the cost tracks that hasher exactly
    // for any type or tuned options (ADR-0012). Double-checked locking guarantees a single
    // computation; volatile guards the lock-free read.
    private static volatile string? _dummyHash;
    private static readonly object _dummyHashLock = new();

    private readonly IPasswordHasher<ApplicationUser> _hasher;

    public LoginTimingEqualizer(IPasswordHasher<ApplicationUser> hasher) => _hasher = hasher;

    /// <inheritdoc />
    public void SpendVerifyCost(string password)
    {
        if (_dummyHash is null)
        {
            lock (_dummyHashLock)
            {
                _dummyHash ??= _hasher.HashPassword(DummyUser, "login-timing-equalization-dummy");
            }
        }
        _hasher.VerifyHashedPassword(DummyUser, _dummyHash, password);
    }
}
