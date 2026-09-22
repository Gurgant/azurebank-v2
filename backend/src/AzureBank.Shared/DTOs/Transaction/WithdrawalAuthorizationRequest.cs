using AzureBank.Shared.Constants;
using AzureBank.Shared.Validation;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Transaction;

/*
  WHY THE SUMMARY BELOW IS SHORT (the T8 trap, docs/engineering-traps.md).

  An XML <summary> on a DTO or its properties BECOMES the `description` in the published OpenAPI
  document whenever no validation attribute supplies one. On #112 a first draft shipped 1391
  characters of engineering forensics -- including a working attack recipe -- into
  docs/api/openapiv1.json and the generated frontend types of a public repository. Contract prose in
  the summaries; reasoning in comments like this one, which the generator ignores.

  The reasoning, since it belongs somewhere:

  - The fields MIRROR WithdrawRequest attribute for attribute, minus Description. That is not tidiness:
    an authorisation whose validation is LAXER than the operation's is an authorisation for a
    withdrawal that cannot happen. Measured on the transfer mints (#113): an amount of 10.00001 minted
    a 201 that the transfer then refused with 400, leaving a row nothing could ever spend. `[MoneyRange]`
    checks the range and NOT the scale, so the scale rule lives in the FluentValidation validator, and
    both layers are mirrored rather than one.

  - NO Description, deliberately, exactly as the transfer mints omit it. RTS Art. 5(1)(d) invalidates an
    authorisation on a change to "the amount or the payee"; a different memo moves the same money out of
    the same account, so binding it would refuse a withdrawal that the regulation does not.

  - No counterparty field exists because a withdrawal has none: cash leaves the system. That is what makes
    the AMOUNT the only field besides the account that a re-presentation could profitably change, and why
    StepUpBinding.ForWithdrawal binds it rather than rendering 0 the way a closure does (ADR-0049 vs -0056).
*/

/// <summary>
/// Authorises one withdrawal: proves the PIN and returns a reference valid only for this account
/// and this amount.
/// </summary>
public class WithdrawalAuthorizationRequest
{
    [Required]
    [NotEmptyGuid(ErrorMessage = ValidationRules.AccountNotEmptyGuid)]
    public Guid AccountId { get; set; }

    [Required]
    [MoneyRange]
    public decimal Amount { get; set; }

    [Required]
    [Pin]
    public required string Pin { get; set; }
}
