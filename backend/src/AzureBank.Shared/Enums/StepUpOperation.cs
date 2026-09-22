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
    InternalTransfer = 1,

    /// <summary>
    /// DELETE /api/accounts/{id} — closing one of the caller's own accounts; bound to the account,
    /// amount rendered 0 (ADR-0049). The first non-money operation on the rail: a closure has a
    /// subject to bind — the account — where a reveal (a GET) has none, which is why the reveal
    /// stays on the BFF's session model and this does not.
    /// </summary>
    AccountDeletion = 2,

    /// <summary>
    /// POST /api/transactions/withdraw — cash out of one of the caller's own accounts; bound to
    /// the account and the amount, with no counterparty (ADR-0056). The first operation on the
    /// rail whose binding names a sum but no second party: a withdrawal has no payee to rename or
    /// resolve, so <c>ToAccountId</c> and <c>RecipientUserId</c> are both null and the amount is
    /// the only thing besides the account that a re-presentation could try to change.
    /// </summary>
    Withdrawal = 3
}
