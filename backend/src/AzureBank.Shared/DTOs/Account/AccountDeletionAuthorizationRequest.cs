using System.ComponentModel.DataAnnotations;
using AzureBank.Shared.Validation;

namespace AzureBank.Shared.DTOs.Account;

/*
  SHORT SUMMARY, for the same reason as the transfer mint DTOs (the T8 trap): the <summary> below
  becomes the schema description in docs/api/openapiv1.json and the generated frontend types.
  Reasoning lives here, where the generator cannot see it.

  - The account is in the ROUTE (POST /api/accounts/{id}/deletion-authorizations), not in the body,
    so the only field a closure has to prove is the PIN. The same [Pin] attribute as the transfer
    mints — one rule, so a PIN the deletion mint refuses is a PIN the transfer mint refuses.
  - The full type name matters beyond this file: a model-state failure is keyed by the DTO's
    namespace-qualified name in the 400 body, and the SPA mock keys on that string (PR-2).
  - Minting IS the authentication event and costs a PIN attempt exactly as a transfer mint does
    (ADR-0042, extended to closures by ADR-0049). The two 422 guards — non-zero balance, primary
    account — run BEFORE the PIN is consulted, so a refused closure never spends an attempt.
*/

/// <summary>
/// Authorises the closure of one account: proves the PIN and returns a reference valid only for
/// DELETE /api/accounts/{id} on that account.
/// </summary>
public class AccountDeletionAuthorizationRequest
{
    [Required]
    [Pin]
    public required string Pin { get; set; }
}
