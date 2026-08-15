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

    /// <summary>
    /// The account password. Required when ENROLLING a PIN (no hash yet), ignored when changing one.
    ///
    /// <para>
    /// The mirror image of <see cref="CurrentPin"/>, and for the same reason: each transition has to
    /// be paid for with a proof the caller could not have obtained from the session alone. Change
    /// costs the old PIN; enrolment costs the password, because there is no old PIN to ask for.
    /// </para>
    /// <para>
    /// Without it, a session cookie WAS the whole proof. Measured end to end through the BFF on
    /// `main` @ 4811667, before this field existed: register → authLevel 1 → set-pin "424242"
    /// (200, cookie only) → verify-pin "424242" → authLevel 2 → deposit 250 → withdraw 250
    /// (201, balanceAfter 0.0000) → GET /full-number → 200 unmasked. Nothing was guessed, so
    /// ADR-0010's attempt-limiting never engaged; ADR-0008's gate checks that A PIN was entered,
    /// not whose.
    /// </para>
    /// <para>
    /// NIST SP 800-63-4B §4.1.2 sets the bar and also caps it: binding a new authenticator SHALL
    /// require authentication at the maximum AAL currently available on the account or the maximum
    /// at which the authenticator will be used, WHICHEVER IS LOWER. With no PIN enrolled the
    /// account's maximum is the password — so the password is required, and nothing heavier is.
    /// </para>
    /// <para>
    /// Not <c>[Required]</c>, for the same reason as <see cref="CurrentPin"/>: which of the two is
    /// mandatory depends on the stored hash, which only <c>AuthService.SetPinAsync</c> can see.
    /// </para>
    /// </summary>
    public string? Password { get; set; }
}
