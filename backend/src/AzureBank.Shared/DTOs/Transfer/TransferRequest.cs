using AzureBank.Shared.Constants;
using AzureBank.Shared.Validation;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Transfer;

public class TransferRequest
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

    /*
      NO PIN HERE ANY MORE, and its absence is the whole point of ADR-0042's second half.

      ADR-0041 put the PIN in this body so the API could refuse a transfer on its own, which closed
      a real hole: before it, the only step-up check lived in the BFF as a session flag, and a call
      made straight to the API carried no second factor at all. But a PIN in the body proves only
      that someone knows six digits — the same six authorise any amount to any payee, which is
      requirement (b) of PSD2-RTS Art. 5 unmet.

      The PIN is now spent at `POST /api/transfers/authorizations`, where it mints an authorisation
      bound to THIS amount and THIS payee and consumable once. The transfer presents that
      authorisation in the `Step-Up-Authorization` header and nothing else. A header rather than a
      field here is load-bearing, not stylistic: the idempotency fingerprint is computed over the
      body alone, so an authorisation carried in the body would make every retry presenting a
      different one a 422 before it reached the endpoint.
    */

    [MaxLength(ValidationRules.TransactionDescriptionMaxLength, ErrorMessage = ValidationRules.DescriptionMaxLengthMessage)]
    public string? Description { get; set; }
}
