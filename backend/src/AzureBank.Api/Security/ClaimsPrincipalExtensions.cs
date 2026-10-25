using System.Security.Claims;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Exceptions;

namespace AzureBank.Api.Security;

/// <summary>Who the caller of a request is, as the API's own bearer token names them.</summary>
internal static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The user id the caller's token carries. A token with none, or with one that is not a
    /// UUID, is refused with 401 <c>AUTH_TOKEN_INVALID</c>, the same answer wherever it is read.
    /// </summary>
    internal static Guid RequireUserId(this ClaimsPrincipal user)
    {
        // The bearer handler maps the token's "sub" to NameIdentifier unless MapInboundClaims is
        // turned off, so the id is under one name or the other.
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value;

        if (!Guid.TryParse(claim, out var userId))
        {
            throw new AuthenticationException("A valid user identity is required.", ErrorCodes.TokenInvalid);
        }

        return userId;
    }
}
