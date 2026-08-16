using System.ComponentModel.DataAnnotations;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Validation;

namespace AzureBank.Shared.DTOs.Transfer;

/*
  WHY THESE SUMMARIES ARE SHORT (the T8 trap, docs/engineering-traps.md).

  An XML <summary> on a DTO property BECOMES that property's `description` in the published OpenAPI
  document whenever no validation attribute supplies one. On #112 a first draft shipped 1391
  characters of engineering forensics — including a working attack recipe — into
  docs/api/openapiv1.json and the generated frontend types of a public repository. Contract prose in
  the summaries; reasoning in comments like this one, which the generator ignores.

  The reasoning, since it belongs somewhere:

  - The fields deliberately MIRROR TransferRequest / InternalTransferRequest, attribute for
    attribute. An authorisation whose validation is laxer than the operation's would be an
    authorisation for a transfer that cannot happen; one that is stricter would refuse a legal
    transfer at mint time. Same attributes is the only version of "the same rules" that cannot drift.
  - Minting IS the authentication event, so it costs a PIN attempt exactly as a transfer does and
    answers with the same codes. A client cannot use this endpoint to probe PINs more cheaply than
    by attempting the transfer itself.
  - The recipient is resolved at mint time, so an authorisation can never name a payee who cannot
    receive. What is stored is their user id, never the handle: handles are renameable (ADR-0015),
    and a binding naming @admin would otherwise outlive @admin being someone else.
  - No Description here, and that is a decision rather than an omission: RTS Art. 5(1)(d)
    invalidates on a change to "the amount or the payee", and a different memo moves the same money
    to the same person.
*/

/// <summary>
/// Authorises one transfer to another user: proves the PIN and returns a reference valid only for
/// this amount and this payee.
/// </summary>
public class TransferAuthorizationRequest
{
    [Required]
    [NotEmptyGuid(ErrorMessage = ValidationRules.AccountNotEmptyGuid)]
    public Guid FromAccountId { get; set; }

    [Required]
    [AzureTag]
    public required string RecipientAzureTag { get; set; }

    [Required]
    [MoneyRange]
    public decimal Amount { get; set; }

    [Required]
    [Pin]
    public required string Pin { get; set; }
}

/// <summary>
/// Authorises one transfer between the caller's own accounts: proves the PIN and returns a reference
/// valid only for this amount and this pair of accounts.
/// </summary>
public class InternalTransferAuthorizationRequest
{
    [Required]
    [NotEmptyGuid(ErrorMessage = ValidationRules.AccountNotEmptyGuid)]
    public Guid FromAccountId { get; set; }

    [Required]
    [NotEmptyGuid(ErrorMessage = ValidationRules.AccountNotEmptyGuid)]
    public Guid ToAccountId { get; set; }

    [Required]
    [MoneyRange]
    public decimal Amount { get; set; }

    [Required]
    [Pin]
    public required string Pin { get; set; }
}

/// <summary>
/// A minted authorisation. Send <c>authorizationId</c> back in the <c>Step-Up-Authorization</c>
/// header of the transfer it authorises; it is accepted once, and only before <c>expiresAt</c>.
/// </summary>
public class StepUpAuthorizationResponse
{
    /// <summary>The authorisation reference to echo back in the request header.</summary>
    public Guid AuthorizationId { get; set; }

    /// <summary>When it stops being spendable (UTC). Not extendable.</summary>
    public DateTime ExpiresAt { get; set; }
}
