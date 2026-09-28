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
    /// Production: 15 min, Development: 10 min
    /// </summary>
    /// <remarks>
    /// 15 since PR-1 (R19, 06 §4.8; 30 before). PSD2's RTS art. 4(3)(d) allows a bank at most 5
    /// minutes, and OWASP says 2-5 for high-value applications; a demo's visitors read code between
    /// clicks, so 5 would sign them out mid-page. 15 halves the time an unattended browser stays signed
    /// in, and ADR-0057 records the gap to PSD2 as a demo deviation. The SPA reads the window from
    /// <c>/bff/auth/me</c>, so no client code holds a copy.
    /// </remarks>
    public int InactivityTimeoutMinutes { get; set; } = 15;

    /// <summary>
    /// Maximum session lifetime regardless of activity.
    /// Production: 60 min, Development: 20 min
    /// </summary>
    public int AbsoluteTimeoutMinutes { get; set; } = 60;
}
