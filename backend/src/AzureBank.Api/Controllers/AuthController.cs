using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.User;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace AzureBank.Api.Controllers;

/// <summary>
/// Authentication controller handling login, registration, logout, and PIN operations.
/// </summary>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IValidator<LoginRequest> _loginValidator;
    private readonly IValidator<RegisterRequest> _registerValidator;
    private readonly IValidator<SetPinRequest> _setPinValidator;
    private readonly IValidator<VerifyPinRequest> _verifyPinValidator;

    public AuthController(
        IAuthService authService,
        IValidator<LoginRequest> loginValidator,
        IValidator<RegisterRequest> registerValidator,
        IValidator<SetPinRequest> setPinValidator,
        IValidator<VerifyPinRequest> verifyPinValidator)
    {
        _authService = authService;
        _loginValidator = loginValidator;
        _registerValidator = registerValidator;
        _setPinValidator = setPinValidator;
        _verifyPinValidator = verifyPinValidator;
    }

    /// <summary>
    /// Authenticate user and receive JWT token.
    /// </summary>
    /// <param name="request">Login credentials</param>
    /// <returns>JWT token and user information</returns>
    [EndpointSummary("Login")]
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<LoginResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)] // ACCOUNT_LOCKED (ADR-0012)
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Login([FromBody] LoginRequest request)
    {
        await _loginValidator.ValidateAndThrowAsync(request);

        var result = await _authService.LoginAsync(request);
        return Ok(ApiResponse<LoginResponse>.Success(result, "Login successful"));
    }

    /// <summary>
    /// Register a new user account with initial bank account.
    /// </summary>
    /// <param name="request">Registration details</param>
    /// <returns>User, account, and token information</returns>
    [EndpointSummary("Register")]
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<RegisterResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<RegisterResponse>>> Register([FromBody] RegisterRequest request)
    {
        await _registerValidator.ValidateAndThrowAsync(request);

        var result = await _authService.RegisterAsync(request);
        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<RegisterResponse>.Success(result, "Registration successful"));
    }

    /// <summary>
    /// Exchange a refresh token for a fresh access + refresh token pair (rotation).
    /// </summary>
    /// <param name="request">The current refresh token</param>
    /// <returns>New access token, new refresh token, and its expiry</returns>
    [EndpointSummary("Refresh access token")]
    [HttpPost("refresh")]
    [AllowAnonymous] // the refresh token IS the credential; the access token may be expired
    [ProducesResponseType(typeof(ApiResponse<RefreshResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)] // invalid/expired/reused
    public async Task<ActionResult<ApiResponse<RefreshResponse>>> Refresh([FromBody] RefreshRequest request)
    {
        var result = await _authService.RefreshAsync(request);
        return Ok(ApiResponse<RefreshResponse>.Success(result, "Token refreshed"));
    }

    /// <summary>
    /// Get current authenticated user information.
    /// </summary>
    /// <returns>User profile information</returns>
    [EndpointSummary("Get current user")]
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<UserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<UserResponse>>> GetCurrentUser()
    {
        var userId = GetCurrentUserId();
        var result = await _authService.GetCurrentUserAsync(userId);
        return Ok(ApiResponse<UserResponse>.Success(result));
    }

    /// <summary>
    /// Logout and invalidate session.
    /// </summary>
    /// <returns>Success message</returns>
    [EndpointSummary("Logout")]
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse>> Logout()
    {
        var userId = GetCurrentUserId();
        await _authService.LogoutAsync(userId);
        return Ok(ApiResponse.Success("Logged out successfully"));
    }

    /// <summary>
    /// Set or update user's PIN for step-up authentication.
    /// </summary>
    /// <param name="request">PIN to set</param>
    /// <returns>Success message</returns>
    [EndpointSummary("Set PIN")]
    [HttpPost("pin")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse>> SetPin([FromBody] SetPinRequest request)
    {
        await _setPinValidator.ValidateAndThrowAsync(request);

        var userId = GetCurrentUserId();
        await _authService.SetPinAsync(userId, request);
        return Ok(ApiResponse.Success("PIN set successfully"));
    }

    /// <summary>
    /// Verify user's PIN for step-up authentication.
    /// </summary>
    /// <param name="request">PIN to verify</param>
    /// <returns>Verification result</returns>
    [EndpointSummary("Verify PIN")]
    [HttpPost("pin/verify")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)] // PIN_LOCKED (ADR-0010)
    public async Task<ActionResult<ApiResponse<object>>> VerifyPin([FromBody] VerifyPinRequest request)
    {
        await _verifyPinValidator.ValidateAndThrowAsync(request);

        var userId = GetCurrentUserId();
        var isValid = await _authService.VerifyPinAsync(userId, request.Pin);

        if (!isValid)
        {
            return Ok(ApiResponse<object>.Success(new { verified = false }, "Invalid PIN"));
        }

        return Ok(ApiResponse<object>.Success(new { verified = true }, "PIN verified"));
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
