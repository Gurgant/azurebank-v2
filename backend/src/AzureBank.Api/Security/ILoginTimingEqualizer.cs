namespace AzureBank.Api.Security;

/// <summary>
/// Spends a fixed amount of password-verification work so a login attempt for a
/// NON-existent account costs the same as one for a real account — closing the
/// user-enumeration timing oracle (ADR-0012).
///
/// Implemented as a singleton: the reference hash is computed exactly ONCE at startup,
/// so an unknown-email login performs a single verification (not a hash + a verify),
/// which keeps the cost equal to <c>UserManager.CheckPasswordAsync</c> and avoids the
/// CPU-doubling an unknown email would otherwise cause.
/// </summary>
public interface ILoginTimingEqualizer
{
    /// <summary>Runs one password verification against a fixed dummy hash (the result is discarded).</summary>
    void SpendVerifyCost(string password);
}
