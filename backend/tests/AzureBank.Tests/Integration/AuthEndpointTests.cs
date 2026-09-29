using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.User;
using AzureBank.Tests.Fixtures;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Integration tests for Authentication endpoints.
/// Tests: /api/auth/register, /api/auth/login, /api/auth/me, /api/auth/refresh, /api/auth/revoke,
/// /api/auth/logout, /api/auth/session-stamps. Which callers the token endpoints answer at all is TokenRoadTests.
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
    public async Task Register_CreatesExactlyOnePrimaryAccount()
    {
        /*
          `AuthServiceTests.RegisterAsync_CreatesDefaultPrimaryAccount` carried this name and an
          empty body under [Fact(Skip = "Requires SQL Server - Account.RowVersion …")]. It could
          never have tested anything, and it could not live in that fixture anyway: the unit test
          mocks UserManager, so no user is ever persisted and there is no account to count.

          What the sibling above asserts is one boolean on the returned DTO. That would still pass
          if registration created three accounts, or created it under the wrong owner, or opened it
          with a non-zero balance. The invariant customers depend on is EXACTLY ONE, so the count
          is asserted against the database rather than the response.
        */
        var request = new RegisterRequest
        {
            AzureTag = $"primary_{Guid.NewGuid().ToString("N")[..8]}",
            Email = $"primary{Guid.NewGuid():N}@example.com",
            Password = "SecurePass123!",
            FirstName = "Prim",
            LastName = "Ary",
        };

        var response = await Client.PostAsJsonAsync("/api/auth/register", request, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions))!.Data!;

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var accounts = await db.Accounts.Where(a => a.UserId == created.User.Id).ToListAsync();

        accounts.Should().HaveCount(1, "registration opens ONE account, not zero and not several");
        var account = accounts[0];
        account.IsPrimary.Should().BeTrue();
        account.Name.Should().Be("Primary Account");
        account.Type.Should().Be(AccountType.Checking);
        account.Balance.Should().Be(0m, "a new account is not credited with anything");
        account.Id.Should().Be(created.Account.Id, "the response names the account that was created");

        // The number is stored raw and only masked on the way out — asserted here because the two
        // are easy to conflate, and storing the masked form would be unrecoverable.
        account.AccountNumber.Should().MatchRegex(@"^AB-\d{4}-\d{4}-\d{2}$");
        created.Account.AccountNumber.Should().MatchRegex(@"^AB-\*{4}-\*{4}-\d{2}$");
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
            Password = "SecurePass123!",
            FirstName = "Alice",
            LastName = "Anders"
        }, JsonOptions);

        var dupHandle = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = existing.AzureTag,
            Email = $"fresh{Guid.NewGuid():N}@example.com",
            Password = "SecurePass123!",
            FirstName = "Bob",
            LastName = "Brown"
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
        result!.Data!.Token.AccessToken.Split('.').Should().HaveCount(3, "the access token is a JWT");
        result.Data.Token.TokenType.Should().Be("Bearer");
        result.Data.User.Email.Should().Be(email);
        // ExpiresAt comes from the token's own exp (JwtOptions.ExpirationMinutes = 15),
        // not a hardcoded literal — so it tracks the real token lifetime.
        result.Data.Token.ExpiresAt.Should().BeCloseTo(
            DateTime.UtcNow.AddMinutes(15), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// The two sign-in endpoints answer ONE token shape. Login used to send the access token as a
    /// bare string with <c>expiresAt</c> and <c>refreshToken</c> beside it, while register nested
    /// all three in <c>token</c> (measured 2026-09-23). Compared as JSON rather than as C# types,
    /// because the JSON is what the BFF, the Bruno collection and CI read: a property added to one
    /// side only is a second shape again, and both DTOs would still compile.
    /// </summary>
    [Fact]
    public async Task Login_AndRegister_AnswerTheSameTokenShape()
    {
        var email = $"shape{Guid.NewGuid():N}@example.com";
        const string password = "SecurePass123!";
        var register = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"shape_{Guid.NewGuid().ToString("N")[..8]}",
            Email = email,
            Password = password,
            FirstName = "Shape",
            LastName = "Test"
        }, JsonOptions);
        register.StatusCode.Should().Be(HttpStatusCode.Created);
        var login = await Client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = password }, JsonOptions);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        using var registered = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        using var loggedIn = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var registerToken = registered.RootElement.GetProperty("data").GetProperty("token");
        var loginData = loggedIn.RootElement.GetProperty("data");

        Shape(loginData.GetProperty("token")).Should().Equal(Shape(registerToken));
        Shape(loginData.GetProperty("token")).Should().Equal(
            ("accessToken", JsonValueKind.String),
            ("expiresAt", JsonValueKind.String),
            ("expiresIn", JsonValueKind.Number),
            ("refreshToken", JsonValueKind.String),
            ("refreshTokenExpiresAt", JsonValueKind.String),
            ("sessionStamp", JsonValueKind.Number),
            ("tokenType", JsonValueKind.String));
        loginData.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["token", "user"], "expiresAt and refreshToken live inside token now, and only there");
    }

    private static List<(string Name, JsonValueKind Kind)> Shape(JsonElement element) =>
        element.EnumerateObject()
            .Select(p => (p.Name, p.Value.ValueKind))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

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
            Email = email,
            Password = password,
            FirstName = "Lock",
            LastName = "Out"
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

    /// <summary>
    /// THE ACCEPTANCE CRITERION for T7/T8, written as the attack it refuses.
    ///
    /// <para>
    /// Measured on `main` @ 4811667 through the real BFF before this existed: a session cookie on an
    /// account that had never enrolled a PIN was enough to choose one, verify it to authLevel 2,
    /// withdraw the whole balance and unmask the account number. Nothing was ever guessed, so
    /// ADR-0010's attempt-limiting never engaged, and ADR-0008's gate only checks that A PIN was
    /// entered — not whose.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SetPin_Enrolling_WithoutPassword_IsRefused()
    {
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Exactly the request that used to succeed: authenticated, well-formed, no password.
        var response = await Client.PostAsJsonAsync(
            "/api/auth/pin", new SetPinRequest { Pin = "424242" }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.PasswordRequired);

        // And the refusal is real, not cosmetic: the PIN it would have bound does not work.
        var verify = await Client.PostAsJsonAsync(
            "/api/auth/pin/verify", new VerifyPinRequest { Pin = "424242" }, JsonOptions);
        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        (await verify.Content.ReadAsStringAsync()).Should()
            .Contain("\"verified\":false", "no PIN was ever enrolled");
    }

    [Fact]
    public async Task SetPin_Enrolling_WithWrongPassword_IsRefused_AndCountsTowardTheLoginLockout()
    {
        /*
          The trap this test exists for: a password check that does NOT count failures turns
          /api/auth/pin into an uncounted password-guessing oracle reachable with a session alone —
          strictly worse than the hole it closes. So the assertion is not merely "401": it is that
          the SAME login lockout engages, which is what makes guessing here no cheaper than guessing
          at /api/auth/login.
        */
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var wrong = new SetPinRequest { Pin = "424242", Password = "NotThePassword1!" };

        for (var i = 0; i < ValidationRules.MaxLoginAttempts - 1; i++)
        {
            (await Client.PostAsJsonAsync("/api/auth/pin", wrong, JsonOptions)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);
        }

        // The crossing attempt locks the account, exactly as the login path would.
        (await Client.PostAsJsonAsync("/api/auth/pin", wrong, JsonOptions)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        // Proven by the CORRECT password now reporting the lock — the login path reveals it only
        // once knowledge of the password is established, and so does this one.
        var correct = new SetPinRequest { Pin = "424242", Password = TestUserPassword };
        var locked = await Client.PostAsJsonAsync("/api/auth/pin", correct, JsonOptions);
        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await locked.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.AccountLocked);
    }

    [Fact]
    public async Task SetPin_Enrolling_WithCorrectPassword_Succeeds_AndTheEnrolledPinWorks()
    {
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var response = await Client.PostAsJsonAsync(
            "/api/auth/pin",
            new SetPinRequest { Pin = "424242", Password = TestUserPassword },
            JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var verify = await Client.PostAsJsonAsync(
            "/api/auth/pin/verify", new VerifyPinRequest { Pin = "424242" }, JsonOptions);
        (await verify.Content.ReadAsStringAsync()).Should().Contain("\"verified\":true");
    }

    [Fact]
    public async Task SetPin_Changing_StillCostsTheOldPin_NotThePassword()
    {
        // The two proofs are not interchangeable: a password does not buy a PIN change, because
        // once a PIN exists the old one is the cheaper and more specific proof to demand.
        var (token, _, _) = await RegisterTestUserAsync();
        await SetPinAsync(token, "123456");

        var withPasswordOnly = await Client.PostAsJsonAsync(
            "/api/auth/pin",
            new SetPinRequest { Pin = "999999", Password = TestUserPassword },
            JsonOptions);

        withPasswordOnly.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await withPasswordOnly.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.PinRequired);
    }

    [Fact]
    public async Task SetPin_WithValidPin_ReturnsOk()
    {
        // Arrange
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Act
        var response = await Client.PostAsJsonAsync("/api/auth/pin", new SetPinRequest
        {
            Password = TestUserPassword,
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

    #region Refresh Tests (ADR-0021, ADR-0057 §4)

    /// <summary>Registers a fresh user and returns its access token, grant and user id.</summary>
    private async Task<(string Access, string Refresh)> RegisterAndGetTokensAsync() =>
        await RegisterAndGetTokensAsync($"rt{Guid.NewGuid().ToString("N")[..8]}@example.com");

    private async Task<(string Access, string Refresh)> RegisterAndGetTokensAsync(string email)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var response = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"rt_{uniqueId}",
            Email = email,
            Password = "SecurePass123!",
            FirstName = "Refresh",
            LastName = "User"
        }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions);
        // RefreshToken is nullable at the contract level but always populated on success.
        return (result!.Data!.Token.AccessToken, result.Data.Token.RefreshToken!);
    }

    /// <summary>A second session of the same user: a login, and its grant.</summary>
    private async Task<string> LoginAndGetGrantAsync(string email)
    {
        var login = await Client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = "SecurePass123!" }, JsonOptions);
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(JsonOptions))!.Data!.Token.RefreshToken!;
    }

    private async Task<HttpResponseMessage> RenewAsync(string grant) =>
        await Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest { RefreshToken = grant }, JsonOptions);

    [Fact]
    public async Task Register_And_Login_IssueRefreshTokens_WithTheirSixtyMinuteExpiry()
    {
        var email = $"rtlogin{Guid.NewGuid():N}@example.com";
        var register = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"rtl_{Guid.NewGuid().ToString("N")[..8]}",
            Email = email,
            Password = "SecurePass123!",
            FirstName = "Ref",
            LastName = "Log"
        }, JsonOptions);
        register.EnsureSuccessStatusCode();
        var registered = (await register.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions))!.Data!.Token;
        registered.RefreshToken.Should().NotBeNullOrEmpty("registration must issue a refresh token");
        registered.RefreshTokenExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(60), TimeSpan.FromMinutes(1),
            "the grant lives 60 minutes from sign-in, and the answer says when (ADR-0057 §4.1)");

        // A subsequent login issues its own (distinct) grant, with its own expiry.
        var login = await Client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = "SecurePass123!" }, JsonOptions);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginToken = (await login.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(JsonOptions))!.Data!.Token;
        loginToken.RefreshToken.Should().NotBeNullOrEmpty("login must issue a refresh token")
            .And.NotBe(registered.RefreshToken);
        loginToken.RefreshTokenExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(60), TimeSpan.FromMinutes(1));
        loginToken.ExpiresAt.Should().BeOnOrBefore(loginToken.RefreshTokenExpiresAt!.Value,
            "the sign-in's access token ends inside its grant's life (ADR-0057 F11)");
    }

    [Fact]
    public async Task Refresh_WithValidToken_ReturnsAnAccessToken_AndNoRefreshToken()
    {
        var (_, refresh) = await RegisterAndGetTokensAsync();

        var response = await RenewAsync(refresh);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = body.RootElement.GetProperty("data");
        data.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["accessToken", "expiresAt"],
            "the grant is not rotated, so the answer carries no refresh token (ADR-0057 §4.3)");
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();
        // ExpiresAt tracks the access token's own exp (JwtOptions.ExpirationMinutes = 15).
        data.GetProperty("expiresAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddMinutes(15), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Refresh_TheSameGrantTwice_AnswersOkBothTimes()
    {
        // ADR-0057 §10 O0-2 item 1. The grant is one reusable credential per session (ADR-0057 §3,
        // §4.3): a renewal whose answer was lost is simply sent again, with the same grant, and
        // must work. Red on main, where the first renewal rotates the grant away and the second is
        // refused.
        var (_, grant) = await RegisterAndGetTokensAsync();

        var first = await Client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = grant }, JsonOptions);
        var second = await Client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = grant }, JsonOptions);

        first.StatusCode.Should().Be(HttpStatusCode.OK,
            $"a live grant renews (body: {await first.Content.ReadAsStringAsync()})");
        second.StatusCode.Should().Be(HttpStatusCode.OK,
            $"the same grant renews again: renewal writes nothing, so it consumes nothing (body: {await second.Content.ReadAsStringAsync()})");
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
    public async Task Revoke_EndsOnlyThePresentedGrant_AndTheUsersOtherSessionStillRenews()
    {
        // "Esci" on session A, as the BFF sends it (ADR-0057 §4.4): only A's grant ends.
        var email = $"rv{Guid.NewGuid():N}@example.com";
        var (_, sessionA) = await RegisterAndGetTokensAsync(email);
        var sessionB = await LoginAndGetGrantAsync(email);

        var revoke = await Client.PostAsJsonAsync("/api/auth/revoke",
            new RevokeRequest { RefreshTokens = [sessionA] }, JsonOptions);

        revoke.StatusCode.Should().Be(HttpStatusCode.OK, "revoke is anonymous: the grant is the credential");
        (await RenewAsync(sessionA)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await RenewAsync(sessionB)).StatusCode.Should().Be(HttpStatusCode.OK, "the other session is not touched");

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var grants = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.User!.Email == email).ToListAsync();
        grants.Should().ContainSingle(t => t.RevokedAt != null)
            .Which.RevokedReason.Should().Be(RefreshTokenRevokedReason.SessionEnded);
    }

    [Fact]
    public async Task Revoke_OfAnUnknownOrAlreadyRevokedGrant_Is200_AsForALiveOne()
    {
        // RFC 7009 §2.2: the answer does not say whether the grant existed. And repeating it
        // changes nothing, so a retried revoke is harmless.
        var (_, grant) = await RegisterAndGetTokensAsync();
        var revoke = new RevokeRequest { RefreshTokens = [grant] };

        (await Client.PostAsJsonAsync("/api/auth/revoke", revoke, JsonOptions)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Client.PostAsJsonAsync("/api/auth/revoke", revoke, JsonOptions)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Client.PostAsJsonAsync("/api/auth/revoke",
                new RevokeRequest { RefreshTokens = ["never-issued"] }, JsonOptions))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoke_WithNoGrants_Is400()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/revoke",
            new RevokeRequest { RefreshTokens = [] }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Revoke_WithANullGrant_Is400_AndRevokesNothing()
    {
        // The contract publishes the items as strings, and a null names no grant: the request is
        // malformed, not a revocation of an unknown grant. Schemathesis's conformance run posted
        // {"refreshTokens": [null]} and got 200 "Revoked" (measured 2026-09-28): "API accepted
        // schema-violating request". Refused before anything is revoked, the live grant beside it
        // included.
        var (_, grant) = await RegisterAndGetTokensAsync();

        var response = await Client.PostAsJsonAsync("/api/auth/revoke",
            new RevokeRequest { RefreshTokens = [grant, null!] }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RenewAsync(grant)).StatusCode.Should().Be(HttpStatusCode.OK, "a refused request revokes nothing");
    }

    [Fact]
    public async Task Logout_RevokesEveryGrantOfTheUser_AsSignOutEverywhere()
    {
        // /api/auth/logout keeps its effect: every session of the user (ADR-0057 §4.4), with the
        // reason that says so — which is why presenting one of them later is not the tripwire.
        var email = $"lo{Guid.NewGuid():N}@example.com";
        var (access, sessionA) = await RegisterAndGetTokensAsync(email);
        var sessionB = await LoginAndGetGrantAsync(email);

        SetAuthHeader(access);
        (await Client.PostAsync("/api/auth/logout", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        ClearAuthHeader();

        (await RenewAsync(sessionA)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await RenewAsync(sessionB)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        (await db.RefreshTokens.AsNoTracking().Where(t => t.User!.Email == email).ToListAsync())
            .Should().HaveCount(2)
            .And.OnlyContain(t => t.RevokedReason == RefreshTokenRevokedReason.SignOutEverywhere);
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

    #region Session stamps (ADR-0057 §5.3)

    /// <summary>Registers a fresh user: its email, access token, grant, id and session stamp.</summary>
    private async Task<(string Email, string Access, Guid UserId, int Stamp)> RegisterForStampAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"st{unique}@example.com";
        var response = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"st_{unique}",
            Email = email,
            Password = "SecurePass123!",
            FirstName = "Session",
            LastName = "Stamp"
        }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var data = (await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions))!.Data!;
        return (email, data.Token.AccessToken, data.User.Id, data.Token.SessionStamp);
    }

    private async Task<IReadOnlyList<UserSessionStamp>> ReadStampsAsync(params Guid[] userIds)
    {
        var response = await Client.PostAsJsonAsync("/api/auth/session-stamps",
            new SessionStampsRequest { UserIds = userIds }, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ApiResponse<SessionStampsResponse>>(JsonOptions))!.Data!.Stamps;
    }

    private async Task LogoutEverywhereAsync(string access)
    {
        SetAuthHeader(access);
        (await Client.PostAsync("/api/auth/logout", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        ClearAuthHeader();
    }

    [Fact]
    public async Task SessionStamp_StartsAt0_EachLogoutRaisesItBy1_AndASignInAnswersTheCurrentOne()
    {
        var (email, access, userId, registeredStamp) = await RegisterForStampAsync();
        registeredStamp.Should().Be(0, "a new user starts at 0");

        await LogoutEverywhereAsync(access);
        (await ReadStampsAsync(userId)).Should().ContainSingle().Which.SessionStamp.Should().Be(1);

        var login = await Client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = "SecurePass123!" }, JsonOptions);
        login.EnsureSuccessStatusCode();
        var signedIn = (await login.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(JsonOptions))!.Data!.Token;
        signedIn.SessionStamp.Should().Be(1, "a sign-in after the sign-out carries the raised stamp, so it is not ended by it");

        await LogoutEverywhereAsync(signedIn.AccessToken);
        (await ReadStampsAsync(userId)).Single().SessionStamp.Should().Be(2);
    }

    [Fact]
    public async Task Revoke_EndsOneSession_AndDoesNotRaiseTheStamp()
    {
        // "Esci" ends ONE session through /revoke (ADR-0057 §4.6). Raising the stamp there would
        // end every other session of the user within ~20 s (up to 1,000 signed-in users; up to
        // 5 s more for each further 1,000): the sign-out-everywhere PR-1 removed from "Esci".
        var unique = Guid.NewGuid().ToString("N")[..8];
        var (_, grant) = await RegisterAndGetTokensAsync($"rv{unique}@example.com");
        var userId = await UserIdOfAsync($"rv{unique}@example.com");

        (await Client.PostAsJsonAsync("/api/auth/revoke", new RevokeRequest { RefreshTokens = [grant] }, JsonOptions))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await RenewAsync(grant)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the revoke did happen");
        (await ReadStampsAsync(userId)).Single().SessionStamp.Should().Be(0);
    }

    private async Task<Guid> UserIdOfAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
    }

    [Fact]
    public async Task SessionStamps_AnswersEachKnownUser_AndNothingForAnUnknownOne()
    {
        var (_, raisedAccess, raised, _) = await RegisterForStampAsync();
        var (_, _, untouched, _) = await RegisterForStampAsync();
        var unknown = Guid.CreateVersion7();
        await LogoutEverywhereAsync(raisedAccess);

        var stamps = await ReadStampsAsync(raised, untouched, unknown, raised);

        stamps.Should().BeEquivalentTo(new[]
        {
            new UserSessionStamp { UserId = raised, SessionStamp = 1 },
            new UserSessionStamp { UserId = untouched, SessionStamp = 0 },
        }, "one entry per known user, whatever the list repeats; an unknown user is left out");
    }

    [Theory]
    [InlineData("""{"userIds":[]}""")]
    [InlineData("""{"userIds":[null]}""")]
    [InlineData("""{"userIds":["not-a-user-id"]}""")]
    [InlineData("""{}""")]
    public async Task SessionStamps_WithoutAListOfUserIds_Is400(string body)
    {
        var response = await Client.PostAsync("/api/auth/session-stamps",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
    }

    #endregion
}
