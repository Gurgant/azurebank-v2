namespace AzureBank.Bff.Options;

/// <summary>
/// Security configuration for step-up authentication.
/// Bound from appsettings.json "Security" section.
/// </summary>
public class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// How long PIN verification remains valid.
    /// Production: 5 min, Development: 10 min
    /// </summary>
    public int PinValidityMinutes { get; set; } = 5;

    /*
        `MaxPinAttempts` and `LockoutMinutes` used to sit here and were never read. Deleted rather
        than wired, because ADR-0010 considered BFF-side limiting and REJECTED it: "the API stays
        open to a direct (non-BFF) JWT caller". A counter here would be a second, non-authoritative
        one that cannot be atomic across BFF instances and would disagree with the real gate.

        The lockout is API-side and shared by BOTH PIN paths through `IPinVerifier`:
        `ValidationRules.MaxPinAttempts` (3) and `PinLockoutMinutes` (15), enforced in `PinService`
        with an atomic counter (see PinLockoutConcurrencySqlServerTests).
    */
}
