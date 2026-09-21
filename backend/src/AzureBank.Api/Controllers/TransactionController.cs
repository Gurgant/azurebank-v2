using System.Security.Claims;
using AzureBank.Api.Attributes;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Constants;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzureBank.Api.Controllers;

/// <summary>
/// Transaction controller handling deposits, withdrawals, and transaction history.
/// </summary>
[ApiController]
[Route("api/transactions")]
[Authorize]
[Produces("application/json")]
public class TransactionController : ControllerBase
{
    private readonly ITransactionService _transactionService;
    private readonly IValidator<DepositRequest> _depositValidator;
    private readonly IValidator<WithdrawRequest> _withdrawValidator;
    private readonly IValidator<WithdrawalAuthorizationRequest> _withdrawalAuthValidator;

    public TransactionController(
        ITransactionService transactionService,
        IValidator<DepositRequest> depositValidator,
        IValidator<WithdrawRequest> withdrawValidator,
        IValidator<WithdrawalAuthorizationRequest> withdrawalAuthValidator)
    {
        _transactionService = transactionService;
        _depositValidator = depositValidator;
        _withdrawValidator = withdrawValidator;
        _withdrawalAuthValidator = withdrawalAuthValidator;
    }

    /// <summary>
    /// List transactions
    /// </summary>
    /// <remarks>
    /// Get transaction history with filtering and pagination.
    /// </remarks>
    /// <param name="filter">Filter and pagination options</param>
    /// <returns>Paginated list of transactions</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PaginatedResponse<TransactionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaginatedResponse<TransactionResponse>>> GetTransactions([FromQuery] TransactionFilter filter)
    {
        var userId = GetCurrentUserId();
        var result = await _transactionService.GetTransactionsAsync(userId, filter);
        return Ok(result);
    }

    /// <summary>
    /// Transaction summary
    /// </summary>
    /// <remarks>
    /// Get aggregated income/expenses/net and pending count over a date window
    /// (defaults to the current UTC calendar month).
    /// </remarks>
    /// <param name="filter">Optional inclusive date window</param>
    /// <returns>Server-side aggregated totals for the caller's accounts</returns>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(ApiResponse<TransactionSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<TransactionSummaryResponse>>> GetSummary(
        [FromQuery] TransactionSummaryFilter filter)
    {
        var userId = GetCurrentUserId();
        var result = await _transactionService.GetSummaryAsync(userId, filter);
        return Ok(ApiResponse<TransactionSummaryResponse>.Success(result));
    }

    /// <summary>
    /// Get transaction
    /// </summary>
    /// <remarks>
    /// Get a specific transaction by ID.
    /// </remarks>
    /// <param name="id">Transaction ID</param>
    /// <returns>Transaction details</returns>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<TransactionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<TransactionResponse>>> GetTransaction(Guid id)
    {
        var userId = GetCurrentUserId();
        var transaction = await _transactionService.GetTransactionByIdAsync(id, userId);
        return Ok(ApiResponse<TransactionResponse>.Success(transaction));
    }

    /// <summary>
    /// Deposit
    /// </summary>
    /// <remarks>
    /// Deposit money into an account.
    /// </remarks>
    /// <param name="request">Deposit details</param>
    /// <returns>Transaction details and new balance</returns>
    [HttpPost("deposit")]
    [RequireIdempotency]
    [RequestSizeLimit(32_768)] // monetary bodies are <2KB; caps hash/buffer work (ADR-0009)
    [ProducesResponseType(typeof(ApiResponse<DepositResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<DepositResponse>>> Deposit([FromBody] DepositRequest request)
    {
        await _depositValidator.ValidateAndThrowAsync(request);

        var userId = GetCurrentUserId();
        var result = await _transactionService.DepositAsync(userId, request);

        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<DepositResponse>.Success(result, "Deposit successful"));
    }

    /// <summary>
    /// Authorise withdrawal
    /// </summary>
    /// <remarks>
    /// Prove the PIN for one withdrawal and receive the authorisation to present on it.
    /// The authorisation is valid only for this account and this amount, is accepted once,
    /// and expires.
    /// </remarks>
    /// <param name="request">The account, the amount, and the PIN</param>
    /// <returns>The authorisation reference to send in the Step-Up-Authorization header, and when it expires</returns>
    /*
      NO [RequireIdempotency], for the reasons on the transfer and closure mints: minting creates
      nothing the caller can be charged for, only one of two mints can ever be spent, and the
      endpoint's job is to be easy to call again after a wrong PIN. What a repeat costs is the
      attempt, which is the point.

      THE OPERATION IS IN THE ROUTE, under `withdraw/`, the same shape the transfer mints use
      (`transfers/authorizations`, `transfers/internal/authorizations`) rather than a bare
      `authorizations` hanging off the controller: this controller also serves deposits and history,
      and a second transaction-scoped authorisation added later must not collide with this one.

      422 IS NOT DECLARED HERE. It is declared by BusinessRulesDocumentTransformer, for the reason
      on the closure mint: an attribute OUTRANKS the transformer's entry and would publish the bare
      reason phrase in place of the sentence naming the code (PIN_REQUIRED).
    */
    [HttpPost("withdraw/authorizations")]
    [RequestSizeLimit(32_768)]
    [ProducesResponseType(typeof(ApiResponse<StepUpAuthorizationResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)] // PIN_LOCKED (ADR-0010)
    public async Task<ActionResult<ApiResponse<StepUpAuthorizationResponse>>> AuthoriseWithdrawal(
        [FromBody] WithdrawalAuthorizationRequest request)
    {
        // Same two-layer guard as the transfer mints: DataAnnotations from [ApiController], then
        // FluentValidation, which is the only layer that checks the money SCALE.
        await _withdrawalAuthValidator.ValidateAndThrowAsync(request);

        var result = await _transactionService.AuthoriseWithdrawalAsync(GetCurrentUserId(), request);

        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<StepUpAuthorizationResponse>.Success(result, "Withdrawal authorised"));
    }

    /// <summary>
    /// Withdraw
    /// </summary>
    /// <remarks>
    /// Withdraw money from an account, presenting the authorisation minted for it.
    /// </remarks>
    /// <param name="request">Withdrawal details</param>
    /// <param name="stepUpAuthorizationId">The authorisation reference minted for this account and amount</param>
    /// <returns>Transaction details and new balance</returns>
    [HttpPost("withdraw")]
    [RequireIdempotency]
    [RequireStepUpAuthorization]
    [RequestSizeLimit(32_768)] // monetary bodies are <2KB; caps hash/buffer work (ADR-0009)
    [ProducesResponseType(typeof(ApiResponse<WithdrawResponse>), StatusCodes.Status201Created)]
    // 400 is reachable two ways: a body that fails validation, and a Step-Up-Authorization header
    // that is present but not a UUID, which MVC model binding refuses before this action runs,
    // keyed on the header name and with no errorCode.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    // 401 AUTHORIZATION_REQUIRED: the header is absent or empty. Declared here because, unlike the
    // 422, no transformer entry supplies it for this endpoint.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<WithdrawResponse>>> Withdraw(
        [FromBody] WithdrawRequest request,
        [FromHeader(Name = StepUpConstants.HeaderName)] Guid? stepUpAuthorizationId = null)
    {
        await _withdrawValidator.ValidateAndThrowAsync(request);

        var userId = GetCurrentUserId();
        var result = await _transactionService.WithdrawAsync(userId, request, stepUpAuthorizationId);

        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<WithdrawResponse>.Success(result, "Withdrawal successful"));
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
