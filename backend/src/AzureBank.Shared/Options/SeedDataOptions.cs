namespace AzureBank.Shared.Options;

/// <summary>
/// Configuration options for database seeding.
/// Only used in Development environment.
/// Values come from appsettings.Development.json "SeedData" section.
/// </summary>
public class SeedDataOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json
    /// </summary>
    public const string SectionName = "SeedData";

    /// <summary>
    /// Default password for all seeded test users.
    /// Must meet password policy requirements.
    /// </summary>
    public string DefaultPassword { get; set; } = "Test123!";

    /// <summary>
    /// Default 6-digit PIN for all seeded test users.
    /// Used for step-up authentication (transfers, sensitive operations).
    /// </summary>
    public string DefaultPin { get; set; } = "123456";

    /// <summary>
    /// Email address for the seeded admin user.
    /// </summary>
    public string AdminEmail { get; set; } = "admin@azurebank.dev";
}
