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
}
