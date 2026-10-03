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
/// The six token endpoints — login, register, the demo claim, refresh, revoke and logout — and the
/// stamp feed, session-stamps, carry <see cref="TokenEndpointAttribute"/>: they answer only the
/// BFF's own client over loopback, and 404 to anything else (ADR-0057 §4.2, §5.3,
/// <see cref="TokenRoadMiddleware"/>). The demo claim also carries <see cref="DemoOnlyAttribute"/>:
/// it is 404 on every road while the demo is off (<see cref="DemoEndpointMiddleware"/>). Register
/// carries <see cref="ClosedInDemoAttribute"/>: the same middleware refuses it with 403 while the
/// demo is on.
/// </remarks>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    /*
      EVERY ACTION'S cancellationToken <param> COMES FIRST in its doc block (ADR-0058). The OpenAPI
      generator writes a <param> it cannot match to an operation parameter onto the request body,
      the last one winning, and a CancellationToken is never an operation parameter: placed after
      the body's own <param>, its sentence replaced the body's description in the published
      document. CommittedOpenApiDocumentTests catches it.
    */

    private readonly IAuthService _authService;
    private readonly IDemoClaimService _demoClaimService;
    private readonly IValidator<LoginRequest> _loginValidator;
    private readonly IValidator<RegisterRequest> _registerValidator;
    private readonly IValidator<SetPinRequest> _setPinValidator;
    private readonly IValidator<VerifyPinRequest> _verifyPinValidator;

    public AuthController(
        IAuthService authService,
        IDemoClaimService demoClaimService,
        IValidator<LoginRequest> loginValidator,
        IValidator<RegisterRequest> registerValidator,
        IValidator<SetPinRequest> setPinValidator,
        IValidator<VerifyPinRequest> verifyPinValidator)
    {
        _authService = authService;
        _demoClaimService = demoClaimService;
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
    /// <param name="cancellationToken">The request's token: cancelled by its deadline or by the caller hanging up (ADR-0058).</param>
    /// <param name="request">Login credentials</param>
    /// <returns>JWT token and user information</returns>
    [HttpPost("login")]
    [AllowAnonymous]
    [TokenEndpoint]
    [ProducesResponseType(typeof(ApiResponse<LoginResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)] // ACCOUNT_LOCKED (ADR-0012)
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Login(
        [FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        await _loginValidator.ValidateAndThrowAsync(request, cancellationToken);

        var result = await _authService.LoginAsync(request, cancellationToken);
        return Ok(ApiResponse<LoginResponse>.Success(result, "Login successful"));
    }

    /// <summary>
    /// Register
    /// </summary>
    /// <remarks>
    /// Register a new user account with initial bank account.
    /// </remarks>
    /// <param name="cancellationToken">The request's token: cancelled by its deadline or by the caller hanging up (ADR-0058).</param>
    /// <param name="request">Registration details</param>
    /// <returns>User, account, and token information</returns>
    [HttpPost("register")]
    [AllowAnonymous]
    [TokenEndpoint]
    [ClosedInDemo]
    [ProducesResponseType(typeof(ApiResponse<RegisterResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<RegisterResponse>>> Register(
        [FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        await _registerValidator.ValidateAndThrowAsync(request, cancellationToken);

        var result = await _authService.RegisterAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<RegisterResponse>.Success(result, "Registration successful"));
    }

    /// <summary>
    /// Claim a demo copy
    /// </summary>
    /// <remarks>
    /// On the public demo, take one free demo copy for a visitor and sign in to it: the answer
    /// carries the tokens and the user a login answers, and what signs in to the copy again. The
    /// copy's password exists in this answer only. A 429 names its reason in `errorCode`:
    /// `DEMO_POOL_EMPTY` when no copy is free, `DEMO_DAILY_LIMIT` when this client has claimed as
    /// many copies as one client may in a day, with `retryAfterSeconds`.
    /// </remarks>
    /// <param name="cancellationToken">The request's token: cancelled by its deadline or by the caller hanging up (ADR-0058).</param>
    /// <param name="request">The visitor's address, as the BFF saw it</param>
    /// <returns>Tokens, user information, and the demo copy</returns>
    [HttpPost("demo/claim")]
    [AllowAnonymous] // a visitor has no account yet; the service key, the marker and loopback still apply
    [TokenEndpoint]
    [DemoOnly]
    [ProducesResponseType(typeof(ApiResponse<DemoClaimResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)] // DEMO_POOL_EMPTY, DEMO_DAILY_LIMIT
    public async Task<ActionResult<ApiResponse<DemoClaimResponse>>> ClaimDemoCopy(
        [FromBody] DemoClaimRequest request, CancellationToken cancellationToken)
    {
        var result = await _demoClaimService.ClaimAsync(request, cancellationToken);

        // The answer carries a password, and this is the only time it exists outside its hash: it
        // must never land in a cache, the rule the unmasked account number follows (ASVS 14.3.2).
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        return Ok(ApiResponse<DemoClaimResponse>.Success(result, "Demo copy claimed"));
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
    [NoRequestDeadline] // runs to completion once started (ADR-0057 §4.3, ADR-0058)
    [ProducesResponseType(typeof(ApiResponse<RefreshResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)] // unknown/revoked/expired
    public async Task<ActionResult<ApiResponse<RefreshResponse>>> Refresh([FromBody] RefreshRequest request)
    {
        // No cancellation: a renewal of an active grant only reads, and the audit rows two refusals
        // write — the unknown grant's and the tripwire's — must not be taken back by a caller that
        // hangs up (ADR-0057 §4.3). So no token here, and the request deadline does not apply
        // either ([NoRequestDeadline]): the renewal and its refusals run to their end.
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
    [NoRequestDeadline] // runs to completion once started (ADR-0057 §4.3, ADR-0058)
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
    /// <param name="cancellationToken">The request's token: cancelled by its deadline or by the caller hanging up (ADR-0058).</param>
    /// <param name="request">The users to read</param>
    /// <returns>Each known user's session stamp</returns>
    [HttpPost("session-stamps")]
    [AllowAnonymous] // the BFF asks for every user it holds a session of; the key, the marker and loopback apply
    [TokenEndpoint]
    [ProducesResponseType(typeof(ApiResponse<SessionStampsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<SessionStampsResponse>>> SessionStamps(
        [FromBody] SessionStampsRequest request, CancellationToken cancellationToken)
    {
        var stamps = await _authService.GetSessionStampsAsync(request.UserIds, cancellationToken);
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
    public async Task<ActionResult<ApiResponse<UserResponse>>> GetCurrentUser(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        var result = await _authService.GetCurrentUserAsync(userId, cancellationToken);
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
    [NoRequestDeadline] // runs to completion once started, as revoke does (ADR-0058)
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
    /// <param name="cancellationToken">The request's token: cancelled by its deadline or by the caller hanging up (ADR-0058).</param>
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
    public async Task<ActionResult<ApiResponse>> SetPin(
        [FromBody] SetPinRequest request, CancellationToken cancellationToken)
    {
        await _setPinValidator.ValidateAndThrowAsync(request, cancellationToken);

        var userId = GetCurrentUserId();
        await _authService.SetPinAsync(userId, request, cancellationToken);
        return Ok(ApiResponse.Success("PIN set successfully"));
    }

    /// <summary>
    /// Verify PIN
    /// </summary>
    /// <remarks>
    /// Verify user's PIN for step-up authentication.
    /// </remarks>
    /// <param name="cancellationToken">The request's token: cancelled by its deadline or by the caller hanging up (ADR-0058).</param>
    /// <param name="request">PIN to verify</param>
    /// <returns>Verification result</returns>
    [HttpPost("pin/verify")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)] // PIN_LOCKED (ADR-0010)
    public async Task<ActionResult<ApiResponse<object>>> VerifyPin(
        [FromBody] VerifyPinRequest request, CancellationToken cancellationToken)
    {
        await _verifyPinValidator.ValidateAndThrowAsync(request, cancellationToken);

        var userId = GetCurrentUserId();
        var isValid = await _authService.VerifyPinAsync(userId, request.Pin, cancellationToken);

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
