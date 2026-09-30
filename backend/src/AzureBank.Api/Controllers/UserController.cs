using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using AzureBank.Api.Attributes;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.User;
using AzureBank.Shared.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzureBank.Api.Controllers;

/// <summary>
/// User controller handling user search and recipient lookup for transfers.
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize]
[Produces("application/json")]
public class UserController : ControllerBase
{
    /*
      EVERY ACTION'S cancellationToken <param> COMES FIRST in its doc block (ADR-0058). The OpenAPI
      generator writes a <param> it cannot match to an operation parameter onto the request body,
      the last one winning, and a CancellationToken is never an operation parameter: placed after
      the body's own <param>, its sentence replaced the body's description in the published
      document. CommittedOpenApiDocumentTests catches it.
    */

    private readonly IUserService _userService;

    public UserController(IUserService userService)
    {
        _userService = userService;
    }

    /// <summary>
    /// Look up a recipient by AzureTag
    /// </summary>
    /// <remarks>
    /// Look up a single user by their EXACT AzureTag, to confirm a transfer recipient.
    /// Returns the masked display name (e.g. "Vladislav A.") for confirmation. This is an
    /// exact-match confirmation oracle by design — there is deliberately no substring/prefix
    /// directory search, which would let an authenticated user harvest the customer list
    /// (ADR-0014; the Zelle/Cash App model).
    /// </remarks>
    /// <param name="cancellationToken">The request's token: cancelled by its deadline or by the caller hanging up (ADR-0058).</param>
    /// <param name="azureTag">Full AzureTag to look up (3-20 chars, AzureTag charset).</param>
    [HttpGet("{azureTag}")]
    [AlwaysFound] // An unknown handle is 200 with exists:false, never 404 (ADR-0014).
    [ProducesResponseType(typeof(ApiResponse<RecipientLookupResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<RecipientLookupResponse>>> GetUserByAzureTag(
        [Required][AzureTagQuery] string azureTag, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        var result = await _userService.GetUserByAzureTagAsync(azureTag, userId, cancellationToken);
        return Ok(ApiResponse<RecipientLookupResponse>.Success(result));
    }

    /// <summary>
    /// Rename my AzureTag
    /// </summary>
    /// <remarks>
    /// Renames the caller's own public AzureTag handle (ADR-0015). The handle is decoupled
    /// from the login identity (UserName is the immutable user id), so this is a plain update.
    /// The bearer token still carries the old handle in its azure_tag claim until it is
    /// refreshed on next login.
    /// </remarks>
    [HttpPatch("me/azuretag")]
    [ProducesResponseType(typeof(ApiResponse<UpdateAzureTagResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<UpdateAzureTagResponse>>> RenameAzureTag(
        [FromBody] UpdateAzureTagRequest request, CancellationToken cancellationToken)
    {
        var newTag = await _userService.RenameAzureTagAsync(GetCurrentUserId(), request.AzureTag, cancellationToken);
        return Ok(ApiResponse<UpdateAzureTagResponse>.Success(new UpdateAzureTagResponse { AzureTag = newTag }));
    }

    /// <summary>
    /// Extracts the current user ID from JWT claims.
    /// </summary>
    private Guid GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
        return Guid.Parse(userIdClaim!);
    }
}
