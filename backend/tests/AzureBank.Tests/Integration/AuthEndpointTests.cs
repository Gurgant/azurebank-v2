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
        // Raw JSON (bypassing the DTO setter's client-side trim) exercises the SERVER-side
        // normalisation: whitespace-padded names must be stored trimmed.
        var tag = $"trim_{Guid.NewGuid().ToString("N")[..8]}";
        var email = $"trim{Guid.NewGuid():N}@example.com";
        var json = $$"""
            {"azureTag":"{{tag}}","email":"{{email}}","password":"SecurePass123!","firstName":"  Vladislav  ","lastName":"  Aleshaev  "}
            """;

        var response = await Client.PostAsync("/api/auth/register",
            new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions);
        result!.Data!.User.FirstName.Should().Be("Vladislav");
        result.Data.User.LastName.Should().Be("Aleshaev");
    }

    [Fact]
    public async Task Register_WithNameThatTrimsBelowMinLength_ReturnsBadRequest()
    {
        // "  a  " passes the RAW {2,50} length rule, but the server trims BEFORE validating, so
        // the 1-char result is rejected — not silently persisted below the guarantee (CR1).
        var tag = $"short_{Guid.NewGuid().ToString("N")[..8]}";
        var email = $"short{Guid.NewGuid():N}@example.com";
        var json = $$"""
            {"azureTag":"{{tag}}","email":"{{email}}","password":"SecurePass123!","firstName":"  a  ","lastName":"Valid"}
            """;

        var response = await Client.PostAsync("/api/auth/register",
            new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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

    #region Refresh Tests (ADR-0021)

    /// <summary>Registers a fresh user and returns its (access, refresh) token pair.</summary>
    private async Task<(string Access, string Refresh)> RegisterAndGetTokensAsync()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var response = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"rt_{uniqueId}",
            Email = $"rt{uniqueId}@example.com",
            Password = "SecurePass123!",
            FirstName = "Refresh",
            LastName = "User"
        }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions);
        // RefreshToken is nullable at the contract level but always populated on success.
        return (result!.Data!.Token.AccessToken, result.Data.Token.RefreshToken!);
    }

    [Fact]
    public async Task Register_And_Login_IssueRefreshTokens()
    {
        var (_, registerRefresh) = await RegisterAndGetTokensAsync();
        registerRefresh.Should().NotBeNullOrEmpty("registration must issue a refresh token");

        // A subsequent login issues its own (distinct) refresh token.
        var email = $"rtlogin{Guid.NewGuid():N}@example.com";
        (await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"rtl_{Guid.NewGuid().ToString("N")[..8]}",
            Email = email, Password = "SecurePass123!", FirstName = "Ref", LastName = "Log"
        }, JsonOptions)).EnsureSuccessStatusCode();
        var login = await Client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = "SecurePass123!" }, JsonOptions);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginBody = await login.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(JsonOptions);
        loginBody!.Data!.RefreshToken.Should().NotBeNullOrEmpty("login must issue a refresh token");
    }

    [Fact]
    public async Task Refresh_WithValidToken_RotatesAndReturnsNewPair()
    {
        var (_, refresh) = await RegisterAndGetTokensAsync();

        var response = await Client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = refresh }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RefreshResponse>>(JsonOptions);
        result!.Data!.AccessToken.Should().NotBeNullOrEmpty();
        result.Data.RefreshToken.Should().NotBeNullOrEmpty()
            .And.NotBe(refresh, "rotation must hand back a NEW refresh token");
        // ExpiresAt tracks the access token's own exp (JwtOptions.ExpirationMinutes = 15).
        result.Data.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(15), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Refresh_ImmediateReplayWithinGrace_Is401_ButLeavesSuccessorUsable()
    {
        var (_, refresh) = await RegisterAndGetTokensAsync();

        // First rotation succeeds and yields a successor.
        var first = await Client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = refresh }, JsonOptions);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var successor = (await first.Content
            .ReadFromJsonAsync<ApiResponse<RefreshResponse>>(JsonOptions))!.Data!.RefreshToken;

        // Replaying the ORIGINAL token immediately is a benign lost-response retry (within the
        // rotation grace window) → rejected with 401 + the UNIFORM invalid-token code (so it is
        // indistinguishable from an unknown/expired token — no oracle).
        var reuse = await Client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = refresh }, JsonOptions);
        reuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await reuse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions))
            .GetProperty("errorCode").GetString().Should().Be(ErrorCodes.RefreshTokenInvalid);

        // ...but the family is NOT revoked — the successor still rotates (genuine reuse-revoke,
        // aged past the grace window, is proved on the SQL-gated path + in unit tests).
        var successorUse = await Client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = successor }, JsonOptions);
        successorUse.StatusCode.Should().Be(HttpStatusCode.OK,
            "an immediate replay is a benign retry, not theft — it must not revoke the family");
    }

    [Fact]
    public async Task Refresh_WithUnknownToken_Returns401_WithUniformCode()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = "not-a-real-token" }, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // Same code as the replay/expired paths — the response must not reveal WHY it failed.
        (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions))
            .GetProperty("errorCode").GetString().Should().Be(ErrorCodes.RefreshTokenInvalid);
    }

    [Fact]
    public async Task Refresh_AfterLogout_Returns401()
    {
        var (access, refresh) = await RegisterAndGetTokensAsync();

        SetAuthHeader(access);
        (await Client.PostAsync("/api/auth/logout", content: null)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        ClearAuthHeader();

        // Logout revoked the user's refresh tokens, so the still-held token no longer works.
        var response = await Client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = refresh }, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion
}
