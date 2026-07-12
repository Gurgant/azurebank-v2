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
    /// Search for users by AzureTag for transfer recipient lookup.
    /// Returns max 10 results with masked display names.
    /// </summary>
    /// <param name="azureTag">Partial AzureTag to search (min 3 characters, must match AzureTag pattern)</param>
    /// <returns>List of matching users</returns>
    [HttpGet("search")]
    [EndpointSummary("Search users")]
    [ProducesResponseType(typeof(ApiResponse<List<RecipientSearchResult>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<List<RecipientSearchResult>>>> SearchUsers(
        [FromQuery]
        [Required(ErrorMessage = "AzureTag search query is required")]
        [AzureTagQuery]
        string azureTag)
    {
        var userId = GetCurrentUserId();
        var results = await _userService.SearchUsersAsync(azureTag, userId);
        return Ok(ApiResponse<List<RecipientSearchResult>>.Success(results));
    }

    /// <summary>
    /// Look up a specific user by AzureTag for transfer recipient verification.
    /// </summary>
    /// <param name="azureTag">Full AzureTag to look up</param>
    /// <returns>Recipient lookup result</returns>
    [HttpGet("{azureTag}")]
    [EndpointSummary("Get user by AzureTag")]
    [ProducesResponseType(typeof(ApiResponse<RecipientLookupResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RecipientLookupResponse>>> GetUserByAzureTag(string azureTag)
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
