using AzureBank.Shared.Constants;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Bff.DTOs;

/// <summary>
/// Re-authenticate the CURRENT session's user at the absolute session cap.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no email field, and that is the security property.</b> The identity comes from the
/// server-side session, so this endpoint cannot be used to sign in as somebody else. It matters
/// because re-authentication happens <i>in place</i>: the page, its route and any half-filled form
/// stay mounted. An endpoint that accepted an identity would let whoever is at the keyboard put
/// their own session behind another person's screen — the on-screen data would belong to the user
/// who walked away. Making the field absent is a stronger guarantee than checking it matches.
/// </para>
/// <para>
/// <b>Deliberately NOT <c>[Password]</c>, unlike <c>LoginRequest</c>.</b> That attribute enforces
/// the complexity <i>pattern</i>, which is the right thing when a password is being CHOSEN and the
/// wrong thing when one is being VERIFIED. Two consequences here: a wrong guess that fails
/// complexity would answer 400 where every other wrong guess answers 401 — a free oracle — and, far
/// worse, a correct password set under an older policy would be rejected by validation, locking a
/// user out of their own live session with a message about character classes. Length is still
/// bounded, because an unbounded string is a request-size problem regardless.
/// </para>
/// </remarks>
public class BffReauthenticateRequest
{
    [Required]
    [MaxLength(ValidationRules.PasswordMaxLength)]
    public required string Password { get; set; }
}
