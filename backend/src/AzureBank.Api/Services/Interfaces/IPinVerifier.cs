namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Verifies a user's step-up PIN with attempt-limiting (lockout). A deliberately
/// narrow interface (ISP): callers that only need PIN verification — e.g.
/// withdrawals — depend on this rather than the full <see cref="IAuthService"/>.
/// The lockout policy lives in exactly one implementation.
/// </summary>
public interface IPinVerifier
{
    /// <summary>
    /// Verifies the PIN and applies attempt-limiting. Returns <c>true</c> on a
    /// correct PIN (resetting the failure counter), <c>false</c> on a wrong PIN
    /// still under the lockout threshold. Throws
    /// <see cref="AzureBank.Shared.Exceptions.PinLockedException"/> (HTTP 423)
    /// when the PIN is already locked or when this attempt crosses the threshold.
    /// </summary>
    Task<bool> VerifyPinAsync(Guid userId, string pin);
}
