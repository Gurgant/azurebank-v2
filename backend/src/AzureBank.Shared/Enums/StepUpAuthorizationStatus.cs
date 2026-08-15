namespace AzureBank.Shared.Enums;

/// <summary>
/// Lifecycle of a step-up authorisation (ADR-0042).
///
/// Pending → Consumed. There is no Expired member, deliberately: expiry is a comparison against
/// <see cref="Entities.StepUpAuthorization.ExpiresAt"/>, so it is always true without anyone having
/// to write it. A third state would need a sweeper to keep it honest, and a status that lags reality
/// is worse than one derived from a timestamp — this table is also the evidence B3 assembles.
///
/// Rows are never deleted. A consumed authorisation is the record that a specific amount was
/// approved for a specific payee, which is what PSD2 Art. 72 asks a PSP to be able to produce.
/// </summary>
public enum StepUpAuthorizationStatus
{
    /// <summary>Minted and not yet spent. Only a Pending row that has not expired can be consumed.</summary>
    Pending = 0,

    /// <summary>Spent by the operation it authorised. Terminal — an authorisation is accepted once.</summary>
    Consumed = 1
}
