using System.Net;
using System.Net.Http.Json;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.User;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Integration tests for Authentication endpoints.
/// Tests: /api/auth/register, /api/auth/login, /api/auth/me, /api/auth/logout
/// </summary>
public class AuthEndpointTests : IntegrationTestBase
{
    public AuthEndpointTests(CustomWebApplicationFactory factory) : base(factory) { }

    #region Register Tests

    [Fact]
    public async Task Register_WithValidData_ReturnsCreated()
    {
        // Arrange
        var request = new RegisterRequest
        {
            AzureTag = $"newuser_{Guid.NewGuid().ToString("N")[..8]}",
            Email = $"newuser{Guid.NewGuid():N}@example.com",
            Password = "SecurePass123!",
            FirstName = "New",
            LastName = "User"
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/auth/register", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions);
        result.Should().NotBeNull();
        result!.Data.Should().NotBeNull();
        result.Data!.User.Email.Should().Be(request.Email);
        result.Data.User.FirstName.Should().Be(request.FirstName);
        result.Data.Token.AccessToken.Should().NotBeNullOrEmpty();
        result.Data.Account.Should().NotBeNull();
        result.Data.Account.IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_ReturnsConflict()
    {
        // Arrange - Register first user
        var email = $"duplicate{Guid.NewGuid():N}@example.com";
        var request1 = new RegisterRequest
        {
            AzureTag = $"user1_{Guid.NewGuid().ToString("N")[..8]}",
            Email = email,
            Password = "SecurePass123!",
            FirstName = "User",
            LastName = "One"
        };
        await Client.PostAsJsonAsync("/api/auth/register", request1, JsonOptions);

        // Act - Try to register with same email
        var request2 = new RegisterRequest
        {
            AzureTag = $"user2_{Guid.NewGuid().ToString("N")[..8]}",
            Email = email,
            Password = "SecurePass123!",
            FirstName = "User",
            LastName = "Two"
        };
        var response = await Client.PostAsJsonAsync("/api/auth/register", request2, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Register_WithInvalidPassword_ReturnsBadRequest()
    {
        // Arrange
        var request = new RegisterRequest
        {
            AzureTag = $"weak_{Guid.NewGuid().ToString("N")[..8]}",
            Email = $"weak{Guid.NewGuid():N}@example.com",
            Password = "weak", // Too weak
            FirstName = "Weak",
            LastName = "Password"
        };

        // Act
        var response = await Client.PostAsJsonAsync("/api/auth/register", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Login Tests

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsOk()
    {
        // Arrange
        var email = $"login{Guid.NewGuid():N}@example.com";
        var password = "SecurePass123!";

        // Register user first
        await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"login_{Guid.NewGuid().ToString("N")[..8]}",
            Email = email,
            Password = password,
            FirstName = "Login",
            LastName = "Test"
        }, JsonOptions);

        // Act
        var response = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password
        }, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(JsonOptions);
        result!.Data!.Token.Should().NotBeNullOrEmpty();
        result.Data.User.Email.Should().Be(email);
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ReturnsUnauthorized()
    {
        // Act
        var response = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = "nonexistent@example.com",
            Password = "WrongPass123!"
        }, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region GetMe Tests

    [Fact]
    public async Task GetMe_WithValidToken_ReturnsUserInfo()
    {
        // Arrange
        var (token, userId, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Act
        var response = await Client.GetAsync("/api/auth/me");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<UserResponse>>(JsonOptions);
        result!.Data!.UserId.Should().Be(userId);
    }

    [Fact]
    public async Task GetMe_WithoutToken_ReturnsUnauthorized()
    {
        // Arrange
        ClearAuthHeader();

        // Act
        var response = await Client.GetAsync("/api/auth/me");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region PIN Tests

    [Fact]
    public async Task SetPin_WithValidPin_ReturnsOk()
    {
        // Arrange
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Act
        var response = await Client.PostAsJsonAsync("/api/auth/pin", new SetPinRequest
        {
            Pin = "123456"
        }, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task VerifyPin_WithCorrectPin_ReturnsOk()
    {
        // Arrange
        var (token, _, _) = await RegisterTestUserAsync();
        await SetPinAsync(token, "123456");

        // Act
        var response = await Client.PostAsJsonAsync("/api/auth/pin/verify", new VerifyPinRequest
        {
            Pin = "123456"
        }, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task VerifyPin_WithIncorrectPin_ReturnsOkWithVerifiedFalse()
    {
        // Arrange
        var (token, _, _) = await RegisterTestUserAsync();
        await SetPinAsync(token, "123456");

        // Act
        var response = await Client.PostAsJsonAsync("/api/auth/pin/verify", new VerifyPinRequest
        {
            Pin = "654321" // Wrong PIN
        }, JsonOptions);

        // Assert - contract: wrong PIN is 200 with { verified: false }, not an error
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"verified\":false");
    }

    [Fact]
    public async Task VerifyPin_AfterMaxWrongAttempts_Returns429PinLocked()
    {
        var (token, _, _) = await RegisterTestUserAsync();
        await SetPinAsync(token, "123456");

        // The first MaxPinAttempts-1 wrong attempts stay soft (200 { verified: false }).
        for (var i = 0; i < ValidationRules.MaxPinAttempts - 1; i++)
        {
            var soft = await Client.PostAsJsonAsync("/api/auth/pin/verify",
                new VerifyPinRequest { Pin = "654321" }, JsonOptions);
            soft.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // The attempt that crosses the threshold locks the PIN -> 423 PIN_LOCKED.
        var locked = await Client.PostAsJsonAsync("/api/auth/pin/verify",
            new VerifyPinRequest { Pin = "654321" }, JsonOptions);
        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        locked.Headers.RetryAfter.Should().NotBeNull("a lockout must advertise Retry-After");
        (await locked.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.PinLocked);

        // Even the CORRECT PIN is refused while locked.
        var stillLocked = await Client.PostAsJsonAsync("/api/auth/pin/verify",
            new VerifyPinRequest { Pin = "123456" }, JsonOptions);
        stillLocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    #endregion
}
