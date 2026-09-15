namespace AzureBank.Shared.Utilities;

/// <summary>
/// The one way a secret reaches a log line: its first eight characters, enough to tell two sessions
/// apart in a trace and useless for presenting one (ADR-0017, the log-identifier rule).
/// </summary>
/// <remarks>
/// It replaced two copies of the same slice on 2026-09-11 — one inline at five BFF sites and a
/// private <c>Redact</c> in <c>TokenRefresher</c> — so that "only the prefix" is a property of one
/// method a test can find, not of eleven call sites that each had to remember it.
/// <c>LogPlaceholderClassTests</c> fails a log call that names <c>{SessionId}</c> without it.
/// </remarks>
public static class SecretPrefix
{
    /// <summary>How many leading characters of a secret a log line may carry.</summary>
    public const int Length = 8;

    /// <summary>The first <see cref="Length"/> characters of <paramref name="secret"/>, or all of it when shorter.</summary>
    public static string Of(string secret) => secret[..Math.Min(Length, secret.Length)];
}
