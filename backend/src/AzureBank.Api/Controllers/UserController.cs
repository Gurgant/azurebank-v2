using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
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
    private readonly IUserService _userService;

    public UserController(IUserService userService)
    {
        _userService = userService;
    }

    /// <summary>
    /// Look up a single user by their EXACT AzureTag, to confirm a transfer recipient.
    /// Returns the masked display name (e.g. "Vladislav A.") for confirmation. This is an
    /// exact-match confirmation oracle by design — there is deliberately no substring/prefix
    /// directory search, which would let an authenticated user harvest the customer list
    /// (ADR-0014; the Zelle/Cash App model).
    /// </summary>
    /// <param name="azureTag">Full AzureTag to look up (3-20 chars, AzureTag charset).</param>
    [HttpGet("{azureTag}")]
    [EndpointSummary("Get user by AzureTag")]
    [ProducesResponseType(typeof(ApiResponse<RecipientLookupResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<RecipientLookupResponse>>> GetUserByAzureTag(
        [Required][AzureTagQuery] string azureTag)
    {
        var userId = GetCurrentUserId();
        var result = await _userService.GetUserByAzureTagAsync(azureTag, userId);
        return Ok(ApiResponse<RecipientLookupResponse>.Success(result));
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
