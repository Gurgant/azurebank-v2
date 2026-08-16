namespace AzureBank.Shared.Enums;

/// <summary>
/// The operation a step-up authorisation was minted for (ADR-0042).
///
/// An enum rather than a free string because the value is part of the binding: a typo in a string
/// would not fail to compile, it would silently mint an authorisation nothing can spend. The name
/// also goes into the HMAC, so an authorisation for one operation can never be presented to another.
/// </summary>
public enum StepUpOperation
{
    /// <summary>POST /api/transfers — money to another user's account.</summary>
    Transfer = 0,

    /// <summary>POST /api/transfers/internal — money between two accounts of the same user.</summary>
    InternalTransfer = 1
}
