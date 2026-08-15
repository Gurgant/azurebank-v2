using System.Security.Claims;
using AzureBank.Api.Attributes;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.Constants;
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

    /*
      NO [RequireIdempotency] ON EITHER MINT ENDPOINT, deliberately.

      Minting moves no money and creates nothing the caller can be charged for, so there is nothing
      to deduplicate. A repeated mint simply produces a second authorisation; only one of them can
      ever be spent, because spending is what the single-use guarantee protects. Requiring a key
      here would add a failure mode (400 IDEMPOTENCY_KEY_MISSING) to an endpoint whose whole job is
      to be easy to call again after a wrong PIN.

      What a repeat DOES cost is a PIN attempt, which is the point: minting is the authentication
      event, and it must not be a cheaper oracle than the transfer itself.
    */

    /// <summary>
    /// Authorise a transfer to another user.
    /// </summary>
    [HttpPost("authorizations")]
    [EndpointSummary("Authorise a transfer")]
    [RequestSizeLimit(32_768)]
    [ProducesResponseType(typeof(ApiResponse<StepUpAuthorizationResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<StepUpAuthorizationResponse>>> AuthoriseTransfer(
        [FromBody] TransferAuthorizationRequest request)
    {
        var result = await _transferService.AuthoriseTransferAsync(GetCurrentUserId(), request);

        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<StepUpAuthorizationResponse>.Success(result, "Transfer authorised"));
    }

    /// <summary>
    /// Authorise a transfer between your own accounts.
    /// </summary>
    [HttpPost("internal/authorizations")]
    [EndpointSummary("Authorise an internal transfer")]
    [RequestSizeLimit(32_768)]
    [ProducesResponseType(typeof(ApiResponse<StepUpAuthorizationResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<StepUpAuthorizationResponse>>> AuthoriseInternalTransfer(
        [FromBody] InternalTransferAuthorizationRequest request)
    {
        var result = await _transferService.AuthoriseInternalTransferAsync(GetCurrentUserId(), request);

        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<StepUpAuthorizationResponse>.Success(result, "Internal transfer authorised"));
    }

    /// <summary>
    /// Transfer money to another user's primary account.
    /// </summary>
    /// <param name="request">Transfer details</param>
    /// <param name="stepUpAuthorizationId">Authorisation reference from the Step-Up-Authorization header (ADR-0042)</param>
    /// <returns>Transfer result with new balance</returns>
    [HttpPost]
    [EndpointSummary("Transfer to user")]
    [RequireIdempotency]
    [RequestSizeLimit(32_768)] // monetary bodies are <2KB; caps hash/buffer work (ADR-0009)
    [ProducesResponseType(typeof(ApiResponse<TransferResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    // Declared because the in-band PIN (ADR-0041) makes all three reachable, and a client that
    // does not handle them shows the user a generic failure for a mistyped PIN:
    // 401 a wrong PIN, 422 no PIN enrolled, 429 the ADR-0010 lockout. Measured, not assumed.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<TransferResponse>>> Transfer(
        [FromBody] TransferRequest request,
        [FromHeader(Name = StepUpConstants.HeaderName)] Guid? stepUpAuthorizationId = null)
    {
        await _transferValidator.ValidateAndThrowAsync(request);

        var userId = GetCurrentUserId();
        var result = await _transferService.TransferAsync(userId, request, stepUpAuthorizationId);

        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<TransferResponse>.Success(result, "Transfer successful"));
    }

    /// <summary>
    /// Transfer money between own accounts.
    /// </summary>
    /// <param name="request">Internal transfer details</param>
    /// <param name="stepUpAuthorizationId">Authorisation reference from the Step-Up-Authorization header (ADR-0042)</param>
    /// <returns>Transfer result with both account balances</returns>
    [HttpPost("internal")]
    [EndpointSummary("Internal transfer")]
    [RequireIdempotency]
    [RequestSizeLimit(32_768)] // monetary bodies are <2KB; caps hash/buffer work (ADR-0009)
    [ProducesResponseType(typeof(ApiResponse<InternalTransferResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    // Declared because the in-band PIN (ADR-0041) makes all three reachable, and a client that
    // does not handle them shows the user a generic failure for a mistyped PIN:
    // 401 a wrong PIN, 422 no PIN enrolled, 429 the ADR-0010 lockout. Measured, not assumed.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<InternalTransferResponse>>> InternalTransfer(
        [FromBody] InternalTransferRequest request,
        [FromHeader(Name = StepUpConstants.HeaderName)] Guid? stepUpAuthorizationId = null)
    {
        await _internalTransferValidator.ValidateAndThrowAsync(request);

        var userId = GetCurrentUserId();
        var result = await _transferService.InternalTransferAsync(userId, request, stepUpAuthorizationId);

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
