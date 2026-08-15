using AzureBank.Shared.Validation;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Auth;

public class SetPinRequest
{
    [Required]
    [Pin]
    public required string Pin { get; set; }

    /// <summary>
    /// The PIN currently on the account. Required when one is already set, ignored when enrolling.
    ///
    /// <para>
    /// Not <c>[Required]</c>, deliberately: the rule is conditional and cannot be expressed in the
    /// schema, so it is enforced in <c>AuthService.SetPinAsync</c> where the existing hash is
    /// visible. <see cref="PinAttribute"/> passes on a null value, so the format check applies only
    /// when a value is supplied.
    /// </para>
    /// <para>
    /// Without this, holding a session was enough to REPLACE the PIN and then satisfy every
    /// PIN gate in the system. Measured end to end through the BFF before it existed: register →
    /// authLevel 1 → set-pin "131313" → set-pin "999999" (200, no proof) → verify-pin "999999" →
    /// authLevel 2 → GET /full-number → 200 with the unmasked number. Attempt-limiting (ADR-0010)
    /// never engaged, because nothing was ever guessed.
    /// </para>
    /// </summary>
    [Pin]
    public string? CurrentPin { get; set; }

    /*
      WHY (kept OUT of the XML summary below, and that placement is the point).

      A `<summary>` on a DTO property becomes the property's `description` in the PUBLISHED OpenAPI
      document whenever no validation attribute supplies one. `Pin` and `CurrentPin` carry `[Pin]`,
      whose message becomes their description, so their long histories never reached the wire —
      accidentally. `Password` has no such attribute, so a first draft of this summary shipped 1391
      characters of engineering forensics into `docs/api/openapiv1.json` and into the generated
      frontend types: a working attack recipe, complete with the commit it was measured on, in the
      public contract of a public repository. Recorded in `docs/engineering-traps.md`.

      The reasoning itself, since it belongs somewhere: each PIN transition is paid for with a proof
      the caller could not have taken from the session alone. A change costs the old PIN; an
      enrolment costs the password, because there is no old PIN to ask for. Before this field, a
      session cookie was the entire proof — the full measured chain is in `AuthService.SetPinAsync`,
      next to the branch that enforces it, and in ADR terms it is the half ADR-0040 deferred.

      NIST SP 800-63-4B §4.1.2 sets the bar and also caps it: bind at the maximum AAL currently
      available on the account, or the maximum the new authenticator will be used at, WHICHEVER IS
      LOWER. With no PIN enrolled the account's maximum is the password — required, and nothing
      heavier.

      Not `[Required]`, for the same reason as CurrentPin: which of the two is mandatory depends on
      the stored hash, which only AuthService.SetPinAsync can see.
    */

    /// <summary>
    /// The account password. Required when enrolling a PIN; ignored when changing an existing one,
    /// where <c>currentPin</c> is the proof instead.
    /// </summary>
    public string? Password { get; set; }
}
