using AzureBank.Api.Attributes;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transfer;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel;
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
    IValidator<UpdateAccountRequest> updateValidator,
    IValidator<AccountDeletionAuthorizationRequest> deletionAuthValidator) : ControllerBase
{
    private readonly IAccountService _accountService = accountService;
    private readonly IValidator<CreateAccountRequest> _createValidator = createValidator;
    private readonly IValidator<UpdateAccountRequest> _updateValidator = updateValidator;
    private readonly IValidator<AccountDeletionAuthorizationRequest> _deletionAuthValidator =
        deletionAuthValidator;

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
    /// Reveal the full (unmasked) account number of one owned account.
    /// Every other endpoint returns the masked form; behind the BFF this exact path is
    /// step-up-gated (PIN, auth level 2) and the response must never be cached.
    /// </summary>
    /// <param name="id">Account ID</param>
    /// <returns>The full account number</returns>
    [HttpGet("{id:guid}/full-number")]
    [EndpointSummary("Reveal full account number")]
    [ProducesResponseType(typeof(ApiResponse<AccountNumberResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<AccountNumberResponse>>> GetFullAccountNumber(Guid id)
    {
        var userId = GetCurrentUserId();
        var result = await _accountService.GetFullAccountNumberAsync(id, userId);

        // ASVS 14.3.2: the one response carrying the unmasked number must never land in
        // a browser or intermediary cache (YARP forwards response headers untouched).
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        return Ok(ApiResponse<AccountNumberResponse>.Success(result));
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
    /// Authorise the closure of one owned account (ADR-0049).
    /// The account must be closable — zero balance, not primary — before the PIN is consulted.
    /// </summary>
    /// <param name="id">Account ID</param>
    /// <param name="request">The PIN</param>
    /// <returns>The authorisation reference to present on DELETE, and when it expires</returns>
    /*
      NO [RequireIdempotency], for the reasons on the transfer mints (TransferController, the note
      above AuthoriseTransfer): minting creates nothing the caller can be charged for, only one of
      two mints can ever be spent, and the endpoint's job is to be easy to call again after a wrong
      PIN. What a repeat costs is the attempt, which is the point.

      THE OPERATION IS IN THE ROUTE SEGMENT — deletion-authorizations, not a bare authorizations —
      so a second account-scoped authorisation of another kind can be added later without a
      collision, and the OpenAPI operation reads as what it is (ADR-0049).
    */
    [HttpPost("{id:guid}/deletion-authorizations")]
    [EndpointSummary("Authorise an account closure")]
    [RequestSizeLimit(32_768)]
    [ProducesResponseType(typeof(ApiResponse<StepUpAuthorizationResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    // 422 is declared by BusinessRulesDocumentTransformer, not here, for the reason on
    // DeleteAccount below: an attribute outranks the transformer's entry and would publish the
    // bare reason phrase in place of the sentence naming the three codes (NON_ZERO_BALANCE,
    // PRIMARY_ACCOUNT_DELETE, PIN_REQUIRED) — which is what the document carried until 2026-09-06.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<StepUpAuthorizationResponse>>> AuthoriseDeletion(
        Guid id, [FromBody] AccountDeletionAuthorizationRequest request)
    {
        // Same two-layer guard as the transfer mints: DataAnnotations from [ApiController], then
        // FluentValidation.
        await _deletionAuthValidator.ValidateAndThrowAsync(request);

        var result = await _accountService.AuthoriseDeletionAsync(GetCurrentUserId(), id, request.Pin);

        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<StepUpAuthorizationResponse>.Success(result, "Account closure authorised"));
    }

    /// <summary>
    /// Delete (soft delete) an account.
    /// Balance must be zero and account cannot be primary.
    /// </summary>
    /// <param name="id">Account ID</param>
    /// <param name="stepUpAuthorizationId">The authorisation reference minted for this
    /// account</param>
    /// <returns>Success message</returns>
    [HttpDelete("{id:guid}")]
    [EndpointSummary("Delete account")]
    [RequireStepUpAuthorization]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    // 400 was declared before ADR-0049 and unreachable then (a DELETE has no body to validate);
    // it is reachable now, through a Step-Up-Authorization header that is not a UUID — MVC model
    // binding refuses it before this action runs, keyed on the header name and with no errorCode.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    /*
      401 FOR THE THREE STEP-UP CODES (AUTHORIZATION_REQUIRED, _EXPIRED, _INVALID), declared here
      as TransferController declares them, so the published contract says what the code answers.

      422 IS NOT DECLARED HERE, and not because it cannot happen — NON_ZERO_BALANCE and
      PRIMARY_ACCOUNT_DELETE both answer it, ahead of the presence check. It is declared by
      BusinessRulesDocumentTransformer with a description naming those two rules; an attribute
      here would take precedence in the generated document and replace that sentence with the bare
      reason phrase, which is the prose a client actually needs. The transformer's entry is the
      declaration, and this comment is what keeps the next reader from adding a second one.
    */
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse>> DeleteAccount(
        Guid id,
        [Description("Authorisation reference minted by POST /api/accounts/{id}/deletion-authorizations (ADR-0049). REQUIRED to close an account: presenting none is refused 401 AUTHORIZATION_REQUIRED and recorded; one minted for a transfer, already spent, or not the caller's own is refused 401 AUTHORIZATION_INVALID; one past its window is refused 401 AUTHORIZATION_EXPIRED. The balance and primary-account rules (422) are checked before the header is.")]
        [FromHeader(Name = StepUpConstants.HeaderName)] Guid? stepUpAuthorizationId = null)
    {
        var userId = GetCurrentUserId();
        await _accountService.DeleteAccountAsync(id, userId, stepUpAuthorizationId);
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
