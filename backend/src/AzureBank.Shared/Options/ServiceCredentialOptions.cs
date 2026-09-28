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

    /// <summary>
    /// The marker the BFF's OWN client puts on its calls, and its proxy strips from the browser's
    /// (06 §4.2). The API's token endpoints answer 404 without exactly one, because the key alone
    /// cannot tell the two roads apart: the proxy adds the key to every browser request, and it
    /// reaches the API over the same loopback interface.
    /// </summary>
    public const string TokenRoadHeaderName = "X-AzureBank-Token-Road";

    /// <summary>The value the BFF's own client sends in <see cref="TokenRoadHeaderName"/>.</summary>
    /// <remarks>Not a secret, and the API does not compare it: what counts is that exactly one arrived.</remarks>
    public const string TokenRoadMarker = "bff";

    /// <summary>
    /// The response header the API adds to its refusal of a missing or wrong key (06 §4.7), so the BFF
    /// can tell "the API does not know my key" from a session that ended — the first is a half-applied
    /// key rotation, and must not sign anybody out.
    /// </summary>
    public const string RefusalHeaderName = "X-AzureBank-Refusal";

    /// <summary>The <see cref="RefusalHeaderName"/> value on a key refusal.</summary>
    public const string ServiceCredentialRefusal = "service-credential";

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
