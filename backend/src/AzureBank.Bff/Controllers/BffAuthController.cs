using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Bff.DTOs;
using AzureBank.Bff.Models;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Controllers;

/// <summary>
/// BFF Authentication Controller.
/// Acts as a security gateway between browser and API.
///
/// Security Pattern:
/// - Browser sends credentials to BFF
/// - BFF forwards to API, receives JWT
/// - BFF stores JWT server-side, returns session cookie to browser
/// - JWT never reaches the browser
/// </summary>
[ApiController]
[Route("bff/auth")]
public class BffAuthController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly ISessionService _sessionService;
    private readonly ITokenRefresher _tokenRefresher;
    private readonly BffSessionOptions _sessionOptions;
    private readonly SecurityOptions _securityOptions;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<BffAuthController> _logger;

    // Shared JSON options with enum string converter for API responses
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public BffAuthController(
        IHttpClientFactory httpClientFactory,
        ISessionService sessionService,
        ITokenRefresher tokenRefresher,
        IOptions<BffSessionOptions> sessionOptions,
        IOptions<SecurityOptions> securityOptions,
        IWebHostEnvironment environment,
        ILogger<BffAuthController> logger)
    {
        _httpClient = httpClientFactory.CreateClient("BackendApi");
        _sessionService = sessionService;
        _tokenRefresher = tokenRefresher;
        _sessionOptions = sessionOptions.Value;
        _securityOptions = securityOptions.Value;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Login - forwards to API, stores JWT server-side, returns session cookie.
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(typeof(ApiResponse<BffLoginResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/auth/login", request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return ForwardUpstreamError(response, content);
            }

            var apiResponse = JsonSerializer.Deserialize<ApiResponse<LoginResponse>>(content,
                JsonOptions);

            var loginResponse = apiResponse!.Data!;

            // Create server-side session with the JWT, its refresh token (for silent re-mint),
            // and user info.
            var sessionId = _sessionService.CreateSession(
                loginResponse.Token,
                loginResponse.ExpiresAt,
                loginResponse.RefreshToken,
                loginResponse.User);

            // Set HTTP-only session cookie
            SetSessionCookie(sessionId);

            _logger.LogInformation("User {UserId} logged in via BFF", loginResponse.User.Id);

            // Return user info (WITHOUT the JWT token)
            return Ok(new ApiResponse<BffLoginResponse>
            {
                Data = new BffLoginResponse
                {
                    User = new UserSessionInfo
                    {
                        Id = loginResponse.User.Id,
                        Email = loginResponse.User.Email,
                        FirstName = loginResponse.User.FirstName,
                        LastName = loginResponse.User.LastName,
                        AzureTag = loginResponse.User.AzureTag,
                        HasPin = loginResponse.User.HasPin
                    },
                    ExpiresAt = loginResponse.ExpiresAt
                },
                Message = "Login successful"
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to backend API");
            return Problem(
                title: "Service Unavailable",
                detail: "Service temporarily unavailable",
                statusCode: 503);
        }
    }

    /// <summary>
    /// Register - forwards to API, stores JWT server-side, returns session cookie.
    /// </summary>
    [HttpPost("register")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(typeof(ApiResponse<BffLoginResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/auth/register", request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return ForwardUpstreamError(response, content);
            }

            var apiResponse = JsonSerializer.Deserialize<ApiResponse<RegisterResponse>>(content,
                JsonOptions);

            var registerResponse = apiResponse!.Data!;

            // Create server-side session with the JWT, its refresh token (nullable — registration
            // issues it best-effort), and user info.
            var sessionId = _sessionService.CreateSession(
                registerResponse.Token.AccessToken,
                registerResponse.Token.ExpiresAt,
                registerResponse.Token.RefreshToken,
                registerResponse.User);

            // Set HTTP-only session cookie
            SetSessionCookie(sessionId);

            _logger.LogInformation("User {UserId} registered via BFF", registerResponse.User.Id);

            // Return user info (WITHOUT the JWT token)
            return StatusCode(StatusCodes.Status201Created, new ApiResponse<BffLoginResponse>
            {
                Data = new BffLoginResponse
                {
                    User = new UserSessionInfo
                    {
                        Id = registerResponse.User.Id,
                        Email = registerResponse.User.Email,
                        FirstName = registerResponse.User.FirstName,
                        LastName = registerResponse.User.LastName,
                        AzureTag = registerResponse.User.AzureTag,
                        HasPin = registerResponse.User.HasPin
                    },
                    ExpiresAt = registerResponse.Token.ExpiresAt
                },
                Message = "Registration successful"
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to backend API");
            return Problem(
                title: "Service Unavailable",
                detail: "Service temporarily unavailable",
                statusCode: 503);
        }
    }

    /// <summary>
    /// Get current user - returns user info and session metadata.
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(ApiResponse<BffMeResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public IActionResult GetCurrentUser()
    {
        var session = GetCurrentSession();
        if (session == null)
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Unauthorized",
                Detail = "Session expired or invalid",
                Status = 401
            });
        }

        var isPinVerified = _sessionService.IsPinVerificationValid(session.SessionId);

        return Ok(new ApiResponse<BffMeResponse>
        {
            Data = new BffMeResponse
            {
                User = session.UserInfo,
                Session = new BffSessionInfo
                {
                    AuthLevel = _sessionService.GetAuthLevel(session.SessionId),
                    CreatedAt = session.SessionCreated,
                    LastActivity = session.LastActivity,
                    ExpiresAt = session.SessionCreated.AddMinutes(_sessionOptions.AbsoluteTimeoutMinutes),
                    IsPinVerified = isPinVerified,
                    PinExpiresAt = isPinVerified
                        ? session.PinVerifiedAt?.AddMinutes(_securityOptions.PinValidityMinutes)
                        : null
                }
            }
        });
    }

    /// <summary>
    /// Logout - revokes session, clears cookie.
    /// </summary>
    [HttpPost("logout")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Logout()
    {
        if (Request.Cookies.TryGetValue(_sessionOptions.CookieName, out var sessionId)
            && !string.IsNullOrEmpty(sessionId))
        {
            // Propagate to the API so it revokes this user's refresh tokens — otherwise logout
            // would end the BFF session but leave the refresh tokens alive server-side. Strictly
            // best-effort: a failure here must never block the local logout.
            await RevokeApiTokensAsync(sessionId);
            _sessionService.RevokeSession(sessionId);
            _logger.LogInformation("User logged out via BFF");
        }

        // The deletion must carry the SAME attributes as the append: browsers match
        // cookies by name+path+attributes, and a __Host- cookie in particular is only
        // evicted by a Secure, Path=/ expiration.
        Response.Cookies.Delete(_sessionOptions.CookieName, BuildSessionCookieOptions());
        return Ok(ApiResponse.Success("Logged out successfully"));
    }

    /// <summary>
    /// Best-effort call to the API's /api/auth/logout so it revokes this user's refresh tokens.
    /// Uses a freshly re-minted access token (the stored one may be within skew). Never throws.
    /// </summary>
    private async Task RevokeApiTokensAsync(string sessionId)
    {
        try
        {
            // Independent, BOUNDED timeout — deliberately NOT HttpContext.RequestAborted: this
            // revocation is the whole point of the call and must complete even if the browser
            // tears down the connection right after sending the logout request.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var accessToken = await _tokenRefresher.GetFreshAccessTokenAsync(sessionId, cts.Token);
            if (string.IsNullOrEmpty(accessToken))
            {
                // No usable token: the session was already dead (its refresh token was revoked or
                // expired), so the API-side tokens are already moot.
                return;
            }

            using var apiRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
            apiRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClient.SendAsync(apiRequest, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "API logout returned {StatusCode} during BFF logout", (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // Strictly best-effort: this must NEVER bubble into Logout() and skip the local session
            // revocation + cookie deletion. A bare OperationCanceledException (e.g. from a gate
            // wait), a malformed response, or any transient are all swallowed here by design.
            _logger.LogWarning(ex, "API logout call failed during BFF logout; proceeding locally");
        }
    }

    /// <summary>
    /// Check session status - used by frontend to verify authentication state.
    /// </summary>
    [HttpGet("session-status")]
    [ProducesResponseType(typeof(BffSessionStatusResponse), StatusCodes.Status200OK)]
    public IActionResult GetSessionStatus()
    {
        var session = GetCurrentSession();

        if (session == null)
        {
            return Ok(new BffSessionStatusResponse
            {
                IsAuthenticated = false,
                AuthLevel = null,
                IsPinVerified = null
            });
        }

        return Ok(new BffSessionStatusResponse
        {
            IsAuthenticated = true,
            AuthLevel = _sessionService.GetAuthLevel(session.SessionId),
            IsPinVerified = _sessionService.IsPinVerificationValid(session.SessionId)
        });
    }

    /// <summary>
    /// Re-mints the session's access token for the PIN paths, which bypass the YARP transform (so
    /// the stored token may be within the skew window). Returns the fresh token, or a ready-to-return
    /// 401 when the session is gone / its refresh token is dead — the single place that 401 is shaped.
    /// </summary>
    private async Task<(string? AccessToken, IActionResult? Unauthorized)> ReMintOrUnauthorizedAsync(
        string sessionId)
    {
        var accessToken = await _tokenRefresher.GetFreshAccessTokenAsync(
            sessionId, HttpContext.RequestAborted);
        if (accessToken is null)
        {
            return (null, Unauthorized(new ProblemDetails
            {
                Title = "Unauthorized",
                Detail = "Session expired or invalid",
                Status = 401
            }));
        }
        return (accessToken, null);
    }

    /// <summary>
    /// Verify PIN - upgrades session to AuthLevel 2 for sensitive operations.
    /// </summary>
    [HttpPost("verify-pin")]
    [ProducesResponseType(typeof(ApiResponse<BffPinVerificationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> VerifyPin([FromBody] VerifyPinRequest request)
    {
        var session = GetCurrentSession();
        if (session == null)
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Unauthorized",
                Detail = "Session expired or invalid",
                Status = 401
            });
        }

        // These paths bypass the YARP transform, so re-mint here too (the access token may be
        // within the skew window).
        var (accessToken, unauthorized) = await ReMintOrUnauthorizedAsync(session.SessionId);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        try
        {
            // Add Authorization header for API call (accessToken is non-null past the guard above).
            using var apiRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin/verify");
            apiRequest.Content = JsonContent.Create(request);
            apiRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", accessToken!);

            var response = await _httpClient.SendAsync(apiRequest);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return ForwardUpstreamError(response, content);
            }

            var apiResponse = JsonSerializer.Deserialize<ApiResponse<PinVerifyResult>>(content,
                JsonOptions);

            var verified = apiResponse?.Data?.Verified ?? false;

            if (verified)
            {
                // Upgrade session to AuthLevel 2
                _sessionService.SetPinVerified(session.SessionId);

                _logger.LogInformation("PIN verified for user {UserId}", session.UserId);

                return Ok(new ApiResponse<BffPinVerificationResponse>
                {
                    Data = new BffPinVerificationResponse
                    {
                        Verified = true,
                        AuthLevel = 2,
                        PinExpiresAt = DateTime.UtcNow.AddMinutes(_securityOptions.PinValidityMinutes)
                    },
                    Message = "PIN verified successfully"
                });
            }

            return Ok(new ApiResponse<BffPinVerificationResponse>
            {
                Data = new BffPinVerificationResponse
                {
                    Verified = false,
                    AuthLevel = 1,
                    PinExpiresAt = null
                },
                Message = "Invalid PIN"
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to verify PIN with backend API");
            return Problem(
                title: "Service Unavailable",
                detail: "Service temporarily unavailable",
                statusCode: 503);
        }
    }

    /// <summary>
    /// Set PIN - forwards to API to set/update user's PIN.
    /// </summary>
    [HttpPost("set-pin")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetPin([FromBody] SetPinRequest request)
    {
        var session = GetCurrentSession();
        if (session == null)
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Unauthorized",
                Detail = "Session expired or invalid",
                Status = 401
            });
        }

        // These paths bypass the YARP transform, so re-mint here too (the access token may be
        // within the skew window).
        var (accessToken, unauthorized) = await ReMintOrUnauthorizedAsync(session.SessionId);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        try
        {
            // Add Authorization header for API call (accessToken is non-null past the guard above).
            using var apiRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin");
            apiRequest.Content = JsonContent.Create(request);
            apiRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", accessToken!);

            var response = await _httpClient.SendAsync(apiRequest);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return ForwardUpstreamError(response, content);
            }

            // Update cached user info to reflect HasPin = true
            _sessionService.UpdateUserInfo(session.SessionId, userInfo => userInfo.HasPin = true);

            _logger.LogInformation("PIN set for user {UserId}", session.UserId);

            return Ok(ApiResponse.Success("PIN set successfully"));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to set PIN with backend API");
            return Problem(
                title: "Service Unavailable",
                detail: "Service temporarily unavailable",
                statusCode: 503);
        }
    }

    #region Helper Methods

    /// <summary>
    /// Gets the current session from cookie.
    /// </summary>
    private UserSession? GetCurrentSession()
    {
        if (Request.Cookies.TryGetValue(_sessionOptions.CookieName, out var sessionId))
        {
            return _sessionService.GetSession(sessionId);
        }
        return null;
    }

    /// <summary>
    /// Sets the session cookie with security attributes.
    /// </summary>
    private void SetSessionCookie(string sessionId)
    {
        Response.Cookies.Append(_sessionOptions.CookieName, sessionId, BuildSessionCookieOptions());
    }

    private CookieOptions BuildSessionCookieOptions()
    {
        return new CookieOptions
        {
            HttpOnly = true,                 // Not accessible via JavaScript
            // The dev browser loop runs over http://localhost (Vite proxy -> BFF), where
            // Safari refuses Secure cookies outright; everywhere else Secure is mandatory
            // (and required by the __Host- prefix applied outside Development).
            Secure = !_environment.IsDevelopment(),
            SameSite = SameSiteMode.Strict,  // CSRF protection
            // No Expires/Max-Age: a SESSION cookie, gone when the browser closes. A bank
            // session persisted to disk until the JWT expiry was wrong; lifetime is
            // enforced server-side anyway (inactivity + absolute timeouts in the store).
            Path = "/"
        };
    }

    /// <summary>
    /// Forwards an upstream error response preserving its status code and JSON body.
    /// A non-JSON body (proxy HTML, empty 502) must not escape as an unhandled 500 —
    /// it becomes a generic 502 ProblemDetails instead.
    /// </summary>
    private IActionResult ForwardUpstreamError(HttpResponseMessage response, string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            // Clone: the element must outlive the disposed document.
            return StatusCode((int)response.StatusCode, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            _logger.LogWarning(
                "Upstream returned a non-JSON error body (status {StatusCode})",
                (int)response.StatusCode);

            // The status line is still trustworthy metadata from OUR API (direct
            // HttpClient, no intermediate proxy) even when the body is not JSON — the
            // framework's bare 401/403/404 responses have EMPTY bodies by contract.
            // Preserve 4xx so status-based client flows (session-expiry on 401) keep
            // working; only 5xx garbage degrades to a generic 502.
            var status = (int)response.StatusCode;
            if (status is >= 400 and < 500)
            {
                return Problem(
                    title: ReasonPhrases.GetReasonPhrase(status),
                    detail: "Upstream service returned an empty or non-JSON response",
                    statusCode: status);
            }

            return Problem(
                title: "Bad Gateway",
                detail: "Upstream service returned an invalid response",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    #endregion
}

/// <summary>
/// Helper class for deserializing PIN verification response.
/// </summary>
internal class PinVerifyResult
{
    public bool Verified { get; set; }
}
