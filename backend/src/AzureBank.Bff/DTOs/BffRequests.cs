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

/// <summary>
/// The body of <c>POST /bff/auth/demo/claim</c>: an empty object.
/// </summary>
/// <remarks>
/// <para>
/// <b>It has no member, and that is the point.</b> The claim needs nothing from the browser: a
/// visitor has no account yet, and the address their copies are counted by is the connection's
/// (<c>ClientAddress.Of</c>), never one a request names. A member here would be a place to name
/// one.
/// </para>
/// <para>
/// <b>It is still a body, and it has to be JSON.</b> <c>[FromBody]</c> on an
/// <c>[ApiController]</c> action answers a request that is not JSON before the action runs: 415
/// to a form, to plain text and to no body at all, 400 to JSON that is not an object
/// (<c>DemoClaimTests.HostileBodies_...</c> holds each). A page of another site can make a
/// browser post a form or plain text without asking first; it cannot make it send JSON. So a
/// claim cannot be started from somebody else's page, also from a browser that sends no
/// <c>Sec-Fetch-Site</c> for the Fetch-Metadata rule to judge by (ADR-0018). From a browser that
/// sends it, that rule refuses the request before the body is looked at
/// (<c>DemoClaimTests.AClaimFromAnotherSite_...</c>).
/// </para>
/// </remarks>
public class BffDemoClaimRequest
{
}
