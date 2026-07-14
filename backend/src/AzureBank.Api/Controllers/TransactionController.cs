using System.Security.Claims;
using AzureBank.Api.Attributes;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
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

    public TransactionController(
        ITransactionService transactionService,
        IValidator<DepositRequest> depositValidator,
        IValidator<WithdrawRequest> withdrawValidator)
    {
        _transactionService = transactionService;
        _depositValidator = depositValidator;
        _withdrawValidator = withdrawValidator;
    }

    /// <summary>
    /// Get transaction history with filtering and pagination.
    /// </summary>
    /// <param name="filter">Filter and pagination options</param>
    /// <returns>Paginated list of transactions</returns>
    [HttpGet]
    [EndpointSummary("List transactions")]
    [ProducesResponseType(typeof(PaginatedResponse<TransactionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaginatedResponse<TransactionResponse>>> GetTransactions([FromQuery] TransactionFilter filter)
    {
        var userId = GetCurrentUserId();
        var result = await _transactionService.GetTransactionsAsync(userId, filter);
        return Ok(result);
    }

    /// <summary>
    /// Get a specific transaction by ID.
    /// </summary>
    /// <param name="id">Transaction ID</param>
    /// <returns>Transaction details</returns>
    [HttpGet("{id:guid}")]
    [EndpointSummary("Get transaction")]
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
    /// Deposit money into an account.
    /// </summary>
    /// <param name="request">Deposit details</param>
    /// <returns>Transaction details and new balance</returns>
    [HttpPost("deposit")]
    [EndpointSummary("Deposit")]
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
    /// Withdraw money from an account.
    /// Requires PIN verification.
    /// </summary>
    /// <param name="request">Withdrawal details including PIN</param>
    /// <returns>Transaction details and new balance</returns>
    [HttpPost("withdraw")]
    [EndpointSummary("Withdraw")]
    [RequireIdempotency]
    [RequestSizeLimit(32_768)] // monetary bodies are <2KB; caps hash/buffer work (ADR-0009)
    [ProducesResponseType(typeof(ApiResponse<WithdrawResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<WithdrawResponse>>> Withdraw([FromBody] WithdrawRequest request)
    {
        await _withdrawValidator.ValidateAndThrowAsync(request);

        var userId = GetCurrentUserId();
        var result = await _transactionService.WithdrawAsync(userId, request);

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
