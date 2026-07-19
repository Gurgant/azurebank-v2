using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task Register_TrimsWhitespaceFromNames()
    {
        // A name with surrounding whitespace passes validation (the charset allows \s) but must
        // be stored trimmed, so masked display ("Vladislav A.") and the profile stay clean.
        var request = new RegisterRequest
        {
            AzureTag = $"trim_{Guid.NewGuid().ToString("N")[..8]}",
            Email = $"trim{Guid.NewGuid():N}@example.com",
            Password = "SecurePass123!",
            FirstName = "  Vladislav  ",
            LastName = "  Aleshaev  "
        };

        var response = await Client.PostAsJsonAsync("/api/auth/register", request, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions);
        result!.Data!.User.FirstName.Should().Be("Vladislav");
        result.Data.User.LastName.Should().Be("Aleshaev");
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

        // Assert - 409, but enumeration-NEUTRAL: no "already registered" / field-specific
        // wording, and the generic REGISTRATION_FAILED code, so an anonymous caller can't
        // read that it was specifically the EMAIL that collided (ADR-0013).
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContainEquivalentOf("already registered");
        body.Should().NotContainEquivalentOf("already taken");
        body.Should().NotContain("DUPLICATE_EMAIL");
        body.Should().NotContain("DUPLICATE_AZURE_TAG");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.RegistrationFailed);
    }

    [Fact]
    public async Task Register_DuplicateEmail_And_DuplicateHandle_AreIndistinguishable()
    {
        // Arrange - one existing user
        var existing = new RegisterRequest
        {
            AzureTag = $"taken_{Guid.NewGuid().ToString("N")[..8]}",
            Email = $"taken{Guid.NewGuid():N}@example.com",
            Password = "SecurePass123!",
            FirstName = "Taken",
            LastName = "User"
        };
        await Client.PostAsJsonAsync("/api/auth/register", existing, JsonOptions);

        // Act - collide on the EMAIL (fresh handle) vs. collide on the HANDLE (fresh email)
        var dupEmail = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"fresh_{Guid.NewGuid().ToString("N")[..8]}",
            Email = existing.Email,
            Password = "SecurePass123!", FirstName = "Alice", LastName = "Anders"
        }, JsonOptions);

        var dupHandle = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = existing.AzureTag,
            Email = $"fresh{Guid.NewGuid():N}@example.com",
            Password = "SecurePass123!", FirstName = "Bob", LastName = "Brown"
        }, JsonOptions);

        // Assert - identical status + detail + code, so the response can't reveal WHICH
        // field collided (the residual 409-vs-201 oracle is documented in ADR-0013).
        dupEmail.StatusCode.Should().Be(HttpStatusCode.Conflict);
        dupHandle.StatusCode.Should().Be(dupEmail.StatusCode);

        var emailBody = await dupEmail.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var handleBody = await dupHandle.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        handleBody.GetProperty("detail").GetString()
            .Should().Be(emailBody.GetProperty("detail").GetString());
        handleBody.GetProperty("errorCode").GetString()
            .Should().Be(emailBody.GetProperty("errorCode").GetString());
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
        // ExpiresAt comes from the token's own exp (JwtOptions.ExpirationMinutes = 15),
        // not a hardcoded literal — so it tracks the real token lifetime.
        result.Data.ExpiresAt.Should().BeCloseTo(
            DateTime.UtcNow.AddMinutes(15), TimeSpan.FromMinutes(1));
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

    [Fact]
    public async Task Login_AfterTooManyWrongPasswords_LocksAccount_Returns429()
    {
        var email = $"lockout{Guid.NewGuid():N}@example.com";
        const string password = "SecurePass123!";
        await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"lock_{Guid.NewGuid().ToString("N")[..8]}",
            Email = email, Password = password, FirstName = "Lock", LastName = "Out"
        }, JsonOptions);

        var wrong = new LoginRequest { Email = email, Password = "WrongPass123!" };

        // Every wrong password stays a generic 401 — even the one that crosses the
        // threshold — so the lock state is never leaked to a password guesser.
        for (var i = 0; i < ValidationRules.MaxLoginAttempts; i++)
        {
            (await Client.PostAsJsonAsync("/api/auth/login", wrong, JsonOptions)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);
        }

        // The CORRECT password is now refused with 429 + Retry-After (account locked).
        var blocked = await Client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = password }, JsonOptions);
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        blocked.Headers.RetryAfter.Should().NotBeNull("a lockout must advertise Retry-After");
        blocked.Headers.RetryAfter!.Delta!.Value.Should().BeCloseTo(
            TimeSpan.FromMinutes(ValidationRules.LoginLockoutMinutes), TimeSpan.FromSeconds(30));
        (await blocked.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.AccountLocked);

        // A wrong password while locked is still the generic 401 (no enumeration signal).
        (await Client.PostAsJsonAsync("/api/auth/login", wrong, JsonOptions)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
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

        // The attempt that crosses the threshold locks the PIN -> 429 PIN_LOCKED.
        var locked = await Client.PostAsJsonAsync("/api/auth/pin/verify",
            new VerifyPinRequest { Pin = "654321" }, JsonOptions);
        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        locked.Headers.RetryAfter.Should().NotBeNull("a lockout must advertise Retry-After");
        locked.Headers.RetryAfter!.Delta.Should().NotBeNull();
        locked.Headers.RetryAfter.Delta!.Value.Should().BeCloseTo(
            TimeSpan.FromMinutes(ValidationRules.PinLockoutMinutes), TimeSpan.FromSeconds(30));
        (await locked.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.PinLocked);

        // Even the CORRECT PIN is refused while locked.
        var stillLocked = await Client.PostAsJsonAsync("/api/auth/pin/verify",
            new VerifyPinRequest { Pin = "123456" }, JsonOptions);
        stillLocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    #endregion
}
