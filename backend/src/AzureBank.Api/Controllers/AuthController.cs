using AzureBank.Api.Attributes;
using AzureBank.Api.Middleware;
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
/// <remarks>
/// The five token endpoints — login, register, refresh, revoke and logout — and the stamp feed,
/// session-stamps, carry <see cref="TokenEndpointAttribute"/>: they answer only the BFF's own client
/// over loopback, and 404 to anything else (ADR-0057 §4.2, §5.3,
/// <see cref="TokenRoadMiddleware"/>).
/// </remarks>
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
    /// Login
    /// </summary>
    /// <remarks>
    /// Authenticate user and receive JWT token.
    /// </remarks>
    /// <param name="request">Login credentials</param>
    /// <returns>JWT token and user information</returns>
    [HttpPost("login")]
    [AllowAnonymous]
    [TokenEndpoint]
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
    /// Register
    /// </summary>
    /// <remarks>
    /// Register a new user account with initial bank account.
    /// </remarks>
    /// <param name="request">Registration details</param>
    /// <returns>User, account, and token information</returns>
    [HttpPost("register")]
    [AllowAnonymous]
    [TokenEndpoint]
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
    /// Refresh access token
    /// </summary>
    /// <remarks>
    /// Present the session's grant for a fresh access token, which expires no later than the grant.
    /// The grant is not consumed: the same one renews again until its session ends or it expires,
    /// and the answer carries no refresh token.
    /// </remarks>
    /// <param name="request">The session's grant</param>
    /// <returns>New access token and its expiry</returns>
    [HttpPost("refresh")]
    [AllowAnonymous] // the grant IS the credential; the access token may be expired
    [TokenEndpoint]
    [ProducesResponseType(typeof(ApiResponse<RefreshResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)] // unknown/revoked/expired
    public async Task<ActionResult<ApiResponse<RefreshResponse>>> Refresh([FromBody] RefreshRequest request)
    {
        // No cancellation: a renewal of an active grant only reads, and the audit rows two refusals
        // write — the unknown grant's and the tripwire's — must not be taken back by a caller that
        // hangs up (ADR-0057 §4.3).
        var result = await _authService.RefreshAsync(request, HttpContext.ReceivedAt());
        return Ok(ApiResponse<RefreshResponse>.Success(result, "Token refreshed"));
    }

    /// <summary>
    /// Revoke grants
    /// </summary>
    /// <remarks>
    /// Revoke the grants of sessions that have ended: one, or several when the BFF drains its
    /// sessions on a graceful stop. Revoked and unknown grants get the same 200 (RFC 7009 §2.2), and
    /// repeating the call changes nothing. A 503 means the revocation was not recorded; send it again.
    /// </remarks>
    /// <param name="request">The grants to revoke</param>
    /// <returns>Success message</returns>
    [HttpPost("revoke")]
    [AllowAnonymous] // the grant IS the credential; the service key, the marker and loopback still apply
    [TokenEndpoint]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse>> Revoke([FromBody] RevokeRequest request)
    {
        // No cancellation either: the UPDATE is short and idempotent, and finishing it after the
        // caller hung up only ends the grant sooner. The caller's retry finds it done.
        await _authService.RevokeAsync(request, HttpContext.ReceivedAt());
        return Ok(ApiResponse.Success("Revoked"));
    }

    /// <summary>
    /// Read session stamps
    /// </summary>
    /// <remarks>
    /// The current session stamp of each listed user. Signing a user out of every session raises
    /// theirs, and a session given a lower one at sign-in has been signed out since. An unknown user
    /// has no entry in the answer.
    /// </remarks>
    /// <param name="request">The users to read</param>
    /// <returns>Each known user's session stamp</returns>
    [HttpPost("session-stamps")]
    [AllowAnonymous] // the BFF asks for every user it holds a session of; the key, the marker and loopback apply
    [TokenEndpoint]
    [ProducesResponseType(typeof(ApiResponse<SessionStampsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<SessionStampsResponse>>> SessionStamps(
        [FromBody] SessionStampsRequest request)
    {
        var stamps = await _authService.GetSessionStampsAsync(request.UserIds, HttpContext.RequestAborted);
        return Ok(ApiResponse<SessionStampsResponse>.Success(new SessionStampsResponse { Stamps = stamps }));
    }

    /// <summary>
    /// Get current user
    /// </summary>
    /// <remarks>
    /// Get current authenticated user information.
    /// </remarks>
    /// <returns>User profile information</returns>
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
    /// Logout from every session
    /// </summary>
    /// <remarks>
    /// Sign the user out of every session on every device: every grant of the user is revoked and
    /// the user's session stamp is raised, together. One session ends through revoke instead.
    /// </remarks>
    /// <returns>Success message</returns>
    [HttpPost("logout")]
    [Authorize]
    [TokenEndpoint]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse>> Logout()
    {
        var userId = GetCurrentUserId();
        await _authService.LogoutAsync(userId, HttpContext.ReceivedAt());
        return Ok(ApiResponse.Success("Logged out successfully"));
    }

    /// <summary>
    /// Set or change PIN
    /// </summary>
    /// <remarks>
    /// Enrols a PIN, or changes an existing one. CHANGING requires `currentPin`; enrolling does
    /// not, because the account password already gated getting here. A `currentPin` is verified
    /// with the same attempt-limiting as every other PIN check (ADR-0010), so wrong values count
    /// toward the lockout and a locked PIN cannot be replaced even by supplying the correct one.
    /// See ADR-0040.
    /// </remarks>
    /// <param name="request">PIN to set</param>
    /// <returns>Success message</returns>
    [HttpPost("pin")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    // Declared because a client implementing PIN CHANGE meets all three, and the spec listed none of
    // them: 401 a wrong currentPin, 422 a missing one, 429 the lockout. The 422 is declared by
    // BusinessRulesDocumentTransformer instead, since 2026-09-11: an attribute here outranks that
    // entry and published the bare "Unprocessable Entity", where the entry names both codes.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse>> SetPin([FromBody] SetPinRequest request)
    {
        await _setPinValidator.ValidateAndThrowAsync(request);

        var userId = GetCurrentUserId();
        await _authService.SetPinAsync(userId, request);
        return Ok(ApiResponse.Success("PIN set successfully"));
    }

    /// <summary>
    /// Verify PIN
    /// </summary>
    /// <remarks>
    /// Verify user's PIN for step-up authentication.
    /// </remarks>
    /// <param name="request">PIN to verify</param>
    /// <returns>Verification result</returns>
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
