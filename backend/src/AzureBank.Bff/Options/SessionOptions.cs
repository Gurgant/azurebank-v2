namespace AzureBank.Bff.Options;

/// <summary>
/// Session configuration options.
/// Bound from appsettings.json "Session" section.
/// Named BffSessionOptions to avoid conflict with Microsoft.AspNetCore.Builder.SessionOptions.
/// </summary>
public class BffSessionOptions
{
    public const string SectionName = "Session";

    /// <summary>
    /// Session cookie name.
    /// </summary>
    public string CookieName { get; set; } = ".AzureBank.Session";

    /// <summary>
    /// Session expires after this many minutes of inactivity.
    /// Production: 30 min, Development: 10 min
    /// </summary>
    public int InactivityTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Maximum session lifetime regardless of activity.
    /// Production: 60 min, Development: 20 min
    /// </summary>
    public int AbsoluteTimeoutMinutes { get; set; } = 60;
}
