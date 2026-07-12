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

    /// <summary>
    /// Lock account after this many failed PIN attempts.
    /// </summary>
    public int MaxPinAttempts { get; set; } = 3;

    /// <summary>
    /// Account lockout duration after max failed attempts.
    /// </summary>
    public int LockoutMinutes { get; set; } = 15;
}
