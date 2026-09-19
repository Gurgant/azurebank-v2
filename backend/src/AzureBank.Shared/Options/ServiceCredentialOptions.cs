namespace AzureBank.Shared.Options;

/// <summary>
/// The credential by which the API knows a request comes from the BFF (ADR-0055).
/// Binds to the "ServiceCredential" section, in BOTH hosts: the BFF sends the key, the API
/// compares it.
/// </summary>
public class ServiceCredentialOptions
{
    /// <summary>Configuration section name in appsettings.json.</summary>
    public const string SectionName = "ServiceCredential";

    /// <summary>The request header the key travels in, BFF to API.</summary>
    public const string HeaderName = "X-AzureBank-Service-Key";

    /// <summary>Shortest key either host starts with.</summary>
    public const int MinimumKeyLength = 32;

    /// <summary>
    /// The shared key. MUST be configured (user-secrets / environment), never committed, and the
    /// same value in both hosts. A host without it refuses to start: an API that quietly accepted
    /// every caller because the key was missing would be the hole this closes, reopened by a
    /// deployment mistake.
    /// </summary>
    public string BffKey { get; set; } = string.Empty;

    /// <summary>True when <see cref="BffKey"/> is long enough to start with.</summary>
    public static bool IsUsable(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length >= MinimumKeyLength;
}
