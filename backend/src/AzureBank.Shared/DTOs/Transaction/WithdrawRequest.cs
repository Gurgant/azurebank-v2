using AzureBank.Shared.Constants;
using AzureBank.Shared.Validation;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Transaction;

public class WithdrawRequest
{
    [Required]
    [NotEmptyGuid(ErrorMessage = "A valid account ID is required.")]
    public Guid AccountId { get; set; }

    [Required]
    [MoneyRange]
    public decimal Amount { get; set; }

    /*
      NO PIN HERE SINCE ADR-0056, and its absence is the point rather than an omission.

      The PIN is proved once, at POST /api/transactions/withdraw/authorizations, and what travels
      with the withdrawal is the authorisation minted from it -- in the Step-Up-Authorization
      HEADER, never in this body. That placement is load-bearing for idempotency:
      IdempotencyService.ComputeRequestHashAsync fingerprints the request BODY only, so keeping the
      authorisation out of it is what lets the same withdrawal be resent byte-identically under the
      same Idempotency-Key while carrying a different, expired or absent authorisation. In the body,
      each of those would change the fingerprint and be refused as IDEMPOTENCY_KEY_REUSE (422)
      before the endpoint ever saw it.
    */

    [MaxLength(ValidationRules.TransactionDescriptionMaxLength, ErrorMessage = ValidationRules.DescriptionMaxLengthMessage)]
    public string? Description { get; set; }
}
