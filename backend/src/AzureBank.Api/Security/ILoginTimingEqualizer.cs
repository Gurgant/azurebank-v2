namespace AzureBank.Api.Security;

/// <summary>
/// Spends a fixed amount of password-verification work so a login attempt for a
/// NON-existent account costs the same as one for a real account — closing the
/// user-enumeration timing oracle (ADR-0012).
///
/// The reference hash is cached process-wide and computed lazily on the first
/// unknown-email login; from then on an unknown-email login performs a single
/// verification (not a hash + a verify), keeping the cost equal to
/// <c>UserManager.CheckPasswordAsync</c>. The service is registered scoped and uses
/// the request's own password hasher, so the cost tracks the configured hashing
/// options; only the very first unknown-email login after startup pays the one-time
/// hash + verify.
/// </summary>
public interface ILoginTimingEqualizer
{
    /// <summary>Runs one password verification against a fixed dummy hash (the result is discarded).</summary>
    void SpendVerifyCost(string password);
}
