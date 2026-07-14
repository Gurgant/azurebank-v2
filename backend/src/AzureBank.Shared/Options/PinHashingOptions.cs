namespace AzureBank.Shared.Options;

/// <summary>
/// Configuration for the PIN-hash pepper (ADR-0011).
/// Binds to the "Security" section in configuration.
///
/// The pepper is a high-entropy server-side secret mixed into the Argon2id PIN
/// hash as the RFC 9106 secret value (K, via Konscious <c>KnownSecret</c>). It is
/// kept OUT of the database (user-secrets in dev, Key Vault in prod) so a database
/// dump alone cannot brute-force the low-entropy (~10^6) PIN space offline. The
/// pepper is never written into the stored hash string.
///
/// The API AND the Seeder must be configured with the SAME pepper, otherwise
/// seeded/newly-created PINs cannot be verified.
/// </summary>
public class PinHashingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Security";

    /// <summary>
    /// The PIN pepper (RFC 9106 secret value K). MUST be configured
    /// (user-secrets / environment / Key Vault), at least 32 characters, never
    /// committed and never stored in the database. Absent → the app fails to start.
    /// </summary>
    public string PinPepper { get; set; } = string.Empty;

    /// <summary>
    /// Identifies which pepper produced a hash. Stamped into new PIN hashes as
    /// <c>keyid=N</c> so verification is self-describing and the pepper can be
    /// rotated: bump the id, keep verifying older hashes, and re-hash them on next
    /// use (rehash-on-use). A stored hash WITHOUT a keyid is a legacy (un-peppered)
    /// hash. Default: 1.
    /// </summary>
    public int PinPepperKeyId { get; set; } = 1;
}
