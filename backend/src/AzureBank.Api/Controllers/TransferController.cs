using System.Security.Claims;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transfer;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzureBank.Api.Controllers;

/// <summary>
/// Transfer controller handling external and internal money transfers.
/// </summary>
[ApiController]
[Route("api/transfers")]
[Authorize]
[Produces("application/json")]
public class TransferController : ControllerBase
{
    private readonly ITransferService _transferService;
    private readonly IValidator<TransferRequest> _transferValidator;
    private readonly IValidator<InternalTransferRequest> _internalTransferValidator;

    public TransferController(
        ITransferService transferService,
        IValidator<TransferRequest> transferValidator,
        IValidator<InternalTransferRequest> internalTransferValidator)
    {
        _transferService = transferService;
        _transferValidator = transferValidator;
        _internalTransferValidator = internalTransferValidator;
    }

    /// <summary>
    /// Transfer money to another user's primary account.
    /// </summary>
    /// <param name="request">Transfer details</param>
    /// <returns>Transfer result with new balance</returns>
    [HttpPost]
    [EndpointSummary("Transfer to user")]
    [ProducesResponseType(typeof(ApiResponse<TransferResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<TransferResponse>>> Transfer([FromBody] TransferRequest request)
    {
        await _transferValidator.ValidateAndThrowAsync(request);

        var userId = GetCurrentUserId();
        var result = await _transferService.TransferAsync(userId, request);

        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<TransferResponse>.Success(result, "Transfer successful"));
    }

    /// <summary>
    /// Transfer money between own accounts.
    /// </summary>
    /// <param name="request">Internal transfer details</param>
    /// <returns>Transfer result with both account balances</returns>
    [HttpPost("internal")]
    [EndpointSummary("Internal transfer")]
    [ProducesResponseType(typeof(ApiResponse<InternalTransferResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<InternalTransferResponse>>> InternalTransfer([FromBody] InternalTransferRequest request)
    {
        await _internalTransferValidator.ValidateAndThrowAsync(request);

        var userId = GetCurrentUserId();
        var result = await _transferService.InternalTransferAsync(userId, request);

        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<InternalTransferResponse>.Success(result, "Internal transfer successful"));
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
