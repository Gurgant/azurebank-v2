namespace AzureBank.Shared.Services.Interfaces;

/// <summary>
/// Interface for password/PIN hashing using Argon2id.
///
/// Two hashing profiles are available:
/// - Password: High security (64 MB memory) for general password hashing
/// - PIN: Optimized (19 MB memory) for 6-digit PINs with limited entropy
///
/// Note: User account passwords are handled by ASP.NET Core Identity.
/// This service is used for PIN hashing and any other custom hashing needs.
/// </summary>
public interface IPasswordHasher
{
    #region Password Hashing (64 MB - High Security)

    /// <summary>
    /// Hashes a password using Argon2id with high security parameters.
    /// Memory: 64 MB, Iterations: 3, Parallelism: 4
    /// </summary>
    /// <param name="password">The plain text password to hash</param>
    /// <returns>Argon2id hash in PHC string format</returns>
    string HashPassword(string password);

    /// <summary>
    /// Verifies a password against a stored Argon2id hash.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    /// <param name="hash">The stored Argon2id hash</param>
    /// <param name="password">The plain text password to verify</param>
    /// <returns>True if the password matches the hash</returns>
    bool VerifyPassword(string hash, string password);

    #endregion

    #region PIN Hashing (19 MB - Optimized for Limited Entropy)

    /// <summary>
    /// Hashes a PIN using Argon2id with optimized parameters for limited entropy.
    /// Memory: 19 MB (OWASP Tier 2), Iterations: 2, Parallelism: 4
    ///
    /// Rationale: PINs have limited entropy (6 digits = 1,000,000 combinations).
    /// Memory-hardness provides less benefit for small search spaces.
    /// Lower memory = faster verification (~50ms vs ~300ms) with acceptable security.
    /// </summary>
    /// <param name="pin">The plain text PIN to hash</param>
    /// <returns>Argon2id hash in PHC string format</returns>
    string HashPin(string pin);

    /// <summary>
    /// Verifies a PIN against a stored Argon2id hash.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    /// <param name="hash">The stored Argon2id hash</param>
    /// <param name="pin">The plain text PIN to verify</param>
    /// <returns>True if the PIN matches the hash</returns>
    bool VerifyPin(string hash, string pin);

    /// <summary>
    /// True when a stored PIN hash was produced by an OLDER pepper key — or by no
    /// pepper at all — compared with the currently active pepper (ADR-0011). The
    /// caller should transparently re-hash the PIN on the next SUCCESSFUL verify
    /// (rehash-on-use migration). Always false when PIN peppering is not configured.
    /// </summary>
    /// <param name="hash">The stored Argon2id PIN hash</param>
    /// <returns>True if the PIN should be re-hashed with the active pepper</returns>
    bool PinNeedsRehash(string hash);

    /// <summary>
    /// True when a stored PIN hash is tagged with a pepper key id that is NOT in the
    /// current keyring — i.e. a pepper was retired while hashes still carried its id
    /// (ADR-0011). Such a hash can never verify (fail-closed); this lets the caller
    /// log a distinct operator diagnostic instead of mistaking it for a wrong PIN.
    /// A legacy (un-peppered) hash and a resolvable key id both return false.
    /// </summary>
    /// <param name="hash">The stored Argon2id PIN hash</param>
    /// <returns>True if the hash references a pepper key the keyring does not hold</returns>
    bool PinPepperMissingFor(string hash);

    #endregion
}
