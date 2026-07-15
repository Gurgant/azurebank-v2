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
    private readonly BffSessionOptions _sessionOptions;
    private readonly SecurityOptions _securityOptions;
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
        IOptions<BffSessionOptions> sessionOptions,
        IOptions<SecurityOptions> securityOptions,
        ILogger<BffAuthController> logger)
    {
        _httpClient = httpClientFactory.CreateClient("BackendApi");
        _sessionService = sessionService;
        _sessionOptions = sessionOptions.Value;
        _securityOptions = securityOptions.Value;
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
                return StatusCode((int)response.StatusCode, JsonDocument.Parse(content).RootElement);
            }

            var apiResponse = JsonSerializer.Deserialize<ApiResponse<LoginResponse>>(content,
                JsonOptions);

            var loginResponse = apiResponse!.Data!;

            // Create server-side session with JWT and user info
            var sessionId = _sessionService.CreateSession(
                loginResponse.Token,
                loginResponse.ExpiresAt,
                loginResponse.User);

            // Set HTTP-only session cookie
            SetSessionCookie(sessionId, loginResponse.ExpiresAt);

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
                return StatusCode((int)response.StatusCode, JsonDocument.Parse(content).RootElement);
            }

            var apiResponse = JsonSerializer.Deserialize<ApiResponse<RegisterResponse>>(content,
                JsonOptions);

            var registerResponse = apiResponse!.Data!;

            // Create server-side session with JWT and user info
            var sessionId = _sessionService.CreateSession(
                registerResponse.Token.AccessToken,
                registerResponse.Token.ExpiresAt,
                registerResponse.User);

            // Set HTTP-only session cookie
            SetSessionCookie(sessionId, registerResponse.Token.ExpiresAt);

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
    public IActionResult Logout()
    {
        if (Request.Cookies.TryGetValue(_sessionOptions.CookieName, out var sessionId))
        {
            _sessionService.RevokeSession(sessionId);
            _logger.LogInformation("User logged out via BFF");
        }

        Response.Cookies.Delete(_sessionOptions.CookieName);
        return Ok(ApiResponse.Success("Logged out successfully"));
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

        try
        {
            // Add Authorization header for API call
            using var apiRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin/verify");
            apiRequest.Content = JsonContent.Create(request);
            apiRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", session.AccessToken);

            var response = await _httpClient.SendAsync(apiRequest);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode, JsonDocument.Parse(content).RootElement);
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

        try
        {
            // Add Authorization header for API call
            using var apiRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin");
            apiRequest.Content = JsonContent.Create(request);
            apiRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", session.AccessToken);

            var response = await _httpClient.SendAsync(apiRequest);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode, JsonDocument.Parse(content).RootElement);
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
    private void SetSessionCookie(string sessionId, DateTime expiresAt)
    {
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,           // Not accessible via JavaScript
            Secure = true,             // HTTPS only
            SameSite = SameSiteMode.Strict,  // CSRF protection
            Expires = expiresAt,
            Path = "/"
        };

        Response.Cookies.Append(_sessionOptions.CookieName, sessionId, cookieOptions);
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
