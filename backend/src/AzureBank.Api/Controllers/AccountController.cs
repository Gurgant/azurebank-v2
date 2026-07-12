using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace AzureBank.Api.Controllers;

/// <summary>
/// Account controller handling CRUD operations for bank accounts.
/// </summary>
[ApiController]
[Route("api/accounts")]
[Authorize]
[Produces("application/json")]
public class AccountController(
    IAccountService accountService,
    IValidator<CreateAccountRequest> createValidator,
    IValidator<UpdateAccountRequest> updateValidator) : ControllerBase
{
    private readonly IAccountService _accountService = accountService;
    private readonly IValidator<CreateAccountRequest> _createValidator = createValidator;
    private readonly IValidator<UpdateAccountRequest> _updateValidator = updateValidator;

    /// <summary>
    /// Get all accounts for the authenticated user.
    /// </summary>
    /// <returns>List of user's accounts</returns>
    [HttpGet]
    [EndpointSummary("List accounts")]
    [ProducesResponseType(typeof(ApiResponse<List<AccountResponse>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<List<AccountResponse>>>> GetAccounts()
    {
        var userId = GetCurrentUserId();
        var accounts = await _accountService.GetUserAccountsAsync(userId);
        return Ok(ApiResponse<List<AccountResponse>>.Success(accounts));
    }

    /// <summary>
    /// Get a specific account by ID.
    /// </summary>
    /// <param name="id">Account ID</param>
    /// <returns>Account details</returns>
    [HttpGet("{id:guid}")]
    [EndpointSummary("Get account")]
    [ProducesResponseType(typeof(ApiResponse<AccountResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<AccountResponse>>> GetAccount(Guid id)
    {
        var userId = GetCurrentUserId();
        var account = await _accountService.GetAccountByIdAsync(id, userId);
        return Ok(ApiResponse<AccountResponse>.Success(account));
    }

    /// <summary>
    /// Get account balance (current or historical).
    /// </summary>
    /// <param name="id">Account ID</param>
    /// <param name="at">Optional: Get balance at specific point in time (ISO 8601)</param>
    /// <returns>Balance information</returns>
    [HttpGet("{id:guid}/balance")]
    [EndpointSummary("Get balance")]
    [ProducesResponseType(typeof(ApiResponse<BalanceResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<BalanceResponse>>> GetBalance(Guid id, [FromQuery] DateTime? at = null)
    {
        var userId = GetCurrentUserId();
        var balance = await _accountService.GetBalanceAsync(id, userId, at);
        return Ok(ApiResponse<BalanceResponse>.Success(balance));
    }

    /// <summary>
    /// Create a new bank account.
    /// </summary>
    /// <param name="request">Account creation details</param>
    /// <returns>Created account</returns>
    [HttpPost]
    [EndpointSummary("Create account")]
    [ProducesResponseType(typeof(ApiResponse<AccountResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<AccountResponse>>> CreateAccount([FromBody] CreateAccountRequest request)
    {
        await _createValidator.ValidateAndThrowAsync(request);

        var userId = GetCurrentUserId();
        var account = await _accountService.CreateAccountAsync(userId, request);

        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<AccountResponse>.Success(account, "Account created successfully"));
    }

    /// <summary>
    /// Update account details (name only).
    /// </summary>
    /// <param name="id">Account ID</param>
    /// <param name="request">Update details</param>
    /// <returns>Updated account</returns>
    [HttpPatch("{id:guid}")]
    [EndpointSummary("Update account")]
    [ProducesResponseType(typeof(ApiResponse<AccountResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<AccountResponse>>> UpdateAccount(Guid id, [FromBody] UpdateAccountRequest request)
    {
        await _updateValidator.ValidateAndThrowAsync(request);

        var userId = GetCurrentUserId();
        var account = await _accountService.UpdateAccountAsync(id, userId, request);
        return Ok(ApiResponse<AccountResponse>.Success(account, "Account updated successfully"));
    }

    /// <summary>
    /// Set an account as the primary account.
    /// </summary>
    /// <param name="id">Account ID to set as primary</param>
    /// <returns>Success message</returns>
    [HttpPatch("{id:guid}/set-primary")]
    [EndpointSummary("Set primary account")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse>> SetPrimaryAccount(Guid id)
    {
        var userId = GetCurrentUserId();
        await _accountService.SetPrimaryAccountAsync(userId, id);
        return Ok(ApiResponse.Success("Account set as primary"));
    }

    /// <summary>
    /// Delete (soft delete) an account.
    /// Balance must be zero and account cannot be primary.
    /// </summary>
    /// <param name="id">Account ID</param>
    /// <returns>Success message</returns>
    [HttpDelete("{id:guid}")]
    [EndpointSummary("Delete account")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse>> DeleteAccount(Guid id)
    {
        var userId = GetCurrentUserId();
        await _accountService.DeleteAccountAsync(id, userId);
        return Ok(ApiResponse.Success("Account deleted successfully"));
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
