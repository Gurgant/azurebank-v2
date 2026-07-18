using AzureBank.Api.Mappers;
using AzureBank.Api.Security;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.User;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Services.Interfaces;
using AzureBank.Shared.Utilities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// Authentication service handling login, registration, and PIN operations.
/// Uses ASP.NET Core Identity for user management.
/// </summary>
public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AzureBankDbContext _context;
    private readonly IJwtService _jwtService;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IPinVerifier _pinVerifier;
    private readonly UserMapper _userMapper;
    private readonly AccountMapper _accountMapper;
    private readonly ILoginTimingEqualizer _timingEqualizer;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        AzureBankDbContext context,
        IJwtService jwtService,
        IPasswordHasher passwordHasher,
        IPinVerifier pinVerifier,
        UserMapper userMapper,
        AccountMapper accountMapper,
        ILoginTimingEqualizer timingEqualizer,
        ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _context = context;
        _jwtService = jwtService;
        _passwordHasher = passwordHasher;
        _pinVerifier = pinVerifier;
        _userMapper = userMapper;
        _accountMapper = accountMapper;
        _timingEqualizer = timingEqualizer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // Spend the same DOMINANT (PBKDF2) password-hash cost a real account would, so
            // an unknown email can't be told apart by that latency; the response body is
            // already identical to a wrong password. A smaller write-latency residual on
            // the account-exists path remains (ADR-0012) — bounded by upstream rate limiting.
            _timingEqualizer.SpendVerifyCost(request.Password);
            _logger.LogWarning("Failed login attempt for email {Email}", request.Email);
            throw new AuthenticationException("Invalid email or password.");
        }

        var now = DateTimeOffset.UtcNow;
        var passwordOk = await _userManager.CheckPasswordAsync(user, request.Password);
        // Respect Identity's per-user LockoutEnabled opt-out (matches IsLockedOutAsync):
        // an exempt account (e.g. a service account) is never treated as locked.
        var lockedUntil = user.LockoutEnabled && user.LockoutEnd is { } end && end > now
            ? end
            : (DateTimeOffset?)null;

        if (passwordOk)
        {
            // Correct password on a locked account: reveal the lock ONLY here — the
            // caller has proven knowledge of the password, so the signal carries no
            // enumeration value — and give a precise Retry-After (ADR-0012).
            if (lockedUntil is { } until)
            {
                _logger.LogWarning("Login refused for locked account {UserId} until {Until}", user.Id, until);
                throw AccountLockedException.Until(until, now);
            }

            // Success: clear any accumulated failures / expired lock.
            if (user.AccessFailedCount != 0 || user.LockoutEnd is not null)
            {
                await ResetLoginLockoutAsync(user);
            }

            var tokenResult = _jwtService.GenerateToken(user);
            _logger.LogInformation("User {UserId} logged in successfully", user.Id);
            return new LoginResponse
            {
                Token = tokenResult.AccessToken,
                ExpiresAt = tokenResult.ExpiresAt, // single source of truth (the token's exp)
                User = _userMapper.ToLoginInfo(user)
            };
        }

        // Wrong password. Count it toward the lockout (but not while already locked, so
        // a persistent attacker cannot extend the window), then return the SAME generic
        // 401 as an unknown user — the lock state is never leaked to a password guesser.
        if (lockedUntil is null)
        {
            await IncrementAndMaybeLockLoginAsync(user, now);
        }
        // Wrong password on a KNOWN account: log the stable user id, not the raw email (PII).
        _logger.LogWarning("Failed login attempt for account {UserId}", user.Id);
        throw new AuthenticationException("Invalid email or password.");
    }

    // ---- Atomic login-lockout writers (Identity's native AccessFailedCount / LockoutEnd) ----
    // A single set-based ExecuteUpdate (no read-modify-write, so no lost updates under a
    // burst of parallel wrong passwords). Identity's UserManager.AccessFailedAsync would
    // instead do an optimistic-concurrency read-modify-write whose lost update EF silently
    // folds into IdentityResult.ConcurrencyFailure — letting a parallel burst stay under
    // the threshold and never lock. ExecuteUpdate is relational-only, so the InMemory test
    // provider falls back to an equivalent tracked write (single-threaded there).

    /// <summary>
    /// One atomic failed-login transition: increment AccessFailedCount, and when it crosses
    /// <see cref="ValidationRules.MaxLoginAttempts"/> set LockoutEnd and reset the counter —
    /// all against the row's CURRENT value in a single statement. An EXPIRED lock is cleared;
    /// a FUTURE lock is never cleared, so the threshold cannot be bypassed by parallel bursts.
    /// </summary>
    private async Task IncrementAndMaybeLockLoginAsync(ApplicationUser user, DateTimeOffset now)
    {
        // An account exempt from lockout (LockoutEnabled=false) never accrues lock state,
        // preserving the invariant "LockoutEnd non-null => the account was lockout-eligible"
        // that the read gate relies on. (Diverges from Identity's AccessFailedAsync, which
        // always increments; safe here because the read gate never re-checks the flag mid-count.)
        if (!user.LockoutEnabled)
            return;

        var max = ValidationRules.MaxLoginAttempts;
        var until = now.AddMinutes(ValidationRules.LoginLockoutMinutes);

        if (_context.Database.IsRelational())
        {
            // The WHERE excludes a currently-locked (or exempt) row, so a late concurrent
            // increment that lands AFTER a peer already latched the lock updates zero rows —
            // no residual count survives onto the next window. Because a matched row is
            // therefore always null-or-expired, the LockoutEnd else-branch is just null.
            await _context.Users
                .Where(u => u.Id == user.Id && u.LockoutEnabled && (u.LockoutEnd == null || u.LockoutEnd < now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.AccessFailedCount,
                        u => u.AccessFailedCount + 1 >= max ? 0 : u.AccessFailedCount + 1)
                    .SetProperty(u => u.LockoutEnd,
                        u => u.AccessFailedCount + 1 >= max ? (DateTimeOffset?)until : null)
                    .SetProperty(u => u.UpdatedAt, (DateTime?)DateTime.UtcNow));
            // Detach so the tracked (now-stale) user can't be written back by a later save.
            _context.Entry(user).State = EntityState.Detached;
            return;
        }

        if (user.AccessFailedCount + 1 >= max)
        {
            user.AccessFailedCount = 0;   // the lock window is now authoritative
            user.LockoutEnd = until;
        }
        else
        {
            user.AccessFailedCount += 1;
            if (user.LockoutEnd is { } expired && expired < now)
            {
                user.LockoutEnd = null;
            }
        }
        user.UpdatedAt = DateTime.UtcNow;   // parity with the relational writer's audit bump
        await _context.SaveChangesAsync();
    }

    // On the relational path ExecuteUpdate bypasses the change tracker, so the tracked
    // `user` keeps its stale AccessFailedCount/LockoutEnd. Detach it so a later
    // SaveChanges in the same request (e.g. a future unit-of-work or audit interceptor)
    // can't write those stale values back and silently revert the reset. Subsequent reads
    // (JWT generation, identity mapping) work fine on the detached entity.
    private async Task ResetLoginLockoutAsync(ApplicationUser user)
    {
        if (_context.Database.IsRelational())
        {
            await _context.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.AccessFailedCount, 0)
                    .SetProperty(u => u.LockoutEnd, (DateTimeOffset?)null)
                    .SetProperty(u => u.UpdatedAt, (DateTime?)DateTime.UtcNow));
            _context.Entry(user).State = EntityState.Detached;
            return;
        }

        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.UpdatedAt = DateTime.UtcNow;   // parity with the relational writer's audit bump
        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<RegisterResponse> RegisterAsync(RegisterRequest request)
    {
        // Reject duplicates with a single enumeration-NEUTRAL response so an anonymous
        // caller can't read which field (email or handle) collided — the specific reason
        // is logged server-side only, as a structured SecurityEvent an operator can alert
        // on (ADR-0013). This is defense-in-depth: it removes the plaintext label, but it
        // does NOT close the structural oracle (a duplicate returns 409 while a fresh email
        // returns 201 + a token). Full closure needs the deferred email-confirmation flow.
        var normalizedAzureTag = request.AzureTag.ToLower();
        if (await _userManager.FindByEmailAsync(request.Email) != null)
        {
            _logger.LogWarning(
                "SecurityEvent {SecurityEvent}: registration rejected, email already registered ({Email})",
                "DuplicateRegistration", request.Email);
            throw new ConflictException("Registration could not be completed.", ErrorCodes.RegistrationFailed);
        }
        if (await _context.Users.AnyAsync(u => u.AzureTag == normalizedAzureTag))
        {
            _logger.LogWarning(
                "SecurityEvent {SecurityEvent}: registration rejected, handle already taken ({AzureTag})",
                "DuplicateRegistration", normalizedAzureTag);
            throw new ConflictException("Registration could not be completed.", ErrorCodes.RegistrationFailed);
        }

        // Decouple the login identity from the public handle (ADR-0015): Identity's UserName
        // is the immutable user id (a UUIDv7 — time-sortable, index-friendly), never shown and
        // never a login credential (login is by email), so the AzureTag is left as a plain,
        // renameable public column. Set the Id explicitly so UserName can mirror it here.
        var userId = Guid.CreateVersion7();
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = userId.ToString(),
            Email = request.Email,
            AzureTag = normalizedAzureTag,
            FirstName = request.FirstName,
            LastName = request.LastName,
            EmailConfirmed = true // Skip email verification for MVP
        };

        IdentityResult result;
        try
        {
            result = await _userManager.CreateAsync(user, request.Password);
        }
        catch (DbUpdateException ex)
        {
            // The genuine TOCTOU race: a concurrent registration passed the advisory
            // pre-checks and Identity's validators too, then the unique index rejected this
            // one at write time. Neutralise it to the SAME response as a pre-check duplicate
            // so the race can't be used to enumerate accounts (ADR-0013).
            _logger.LogWarning(ex,
                "SecurityEvent {SecurityEvent}: registration lost the unique-index race",
                "DuplicateRegistration");
            throw new ConflictException("Registration could not be completed.", ErrorCodes.RegistrationFailed);
        }

        if (!result.Succeeded)
        {
            // Never echo Identity's error descriptions to the client. A duplicate that slips
            // past the advisory pre-checks (a race) surfaces here as a Duplicate* code; it
            // must return the SAME neutral 409 as the pre-check path or the differing
            // response re-opens the enumeration oracle (ADR-0013). Branch on the stable
            // error Code, not the localisable Description. Any other failure gets a generic
            // message (it is not an existence oracle).
            var codes = string.Join(",", result.Errors.Select(e => e.Code));
            var isDuplicate = result.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail");
            _logger.LogWarning(
                "SecurityEvent {SecurityEvent}: registration rejected by Identity ({Codes})",
                isDuplicate ? "DuplicateRegistration" : "RegistrationRejected", codes);
            if (isDuplicate)
            {
                throw new ConflictException("Registration could not be completed.", ErrorCodes.RegistrationFailed);
            }
            throw new BusinessRuleException("Registration could not be completed.");
        }

        // Assign default role
        await _userManager.AddToRoleAsync(user, Roles.Default);

        // Create default primary account
        var account = new Account
        {
            UserId = user.Id,
            AccountNumber = IdGenerator.GenerateAccountNumber(),
            Name = "Primary Account",
            Type = AccountType.Checking,
            Balance = 0,
            IsPrimary = true,
            User = user
        };

        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        var tokenResult = _jwtService.GenerateToken(user);

        _logger.LogInformation("User {UserId} registered successfully with account {AccountId}", user.Id, account.Id);

        return new RegisterResponse
        {
            User = _userMapper.ToLoginInfo(user),
            Account = _accountMapper.ToResponse(account),
            Token = new Shared.DTOs.Auth.TokenResponse
            {
                AccessToken = tokenResult.AccessToken,
                ExpiresIn = Math.Max(0, (int)(tokenResult.ExpiresAt - DateTime.UtcNow).TotalSeconds),
                TokenType = "Bearer",
                ExpiresAt = tokenResult.ExpiresAt
            }
        };
    }

    /// <inheritdoc />
    public async Task<UserResponse> GetCurrentUserAsync(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());

        if (user == null)
        {
            throw new NotFoundException("User", userId);
        }

        return _userMapper.ToResponse(user);
    }

    /// <inheritdoc />
    public async Task LogoutAsync(Guid userId)
    {
        // For MVP: Just log the logout
        // Future: Invalidate refresh tokens in database
        _logger.LogInformation("User {UserId} logged out", userId);
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> VerifyPinAsync(Guid userId, string pin) =>
        // Delegated to PinService (attempt-limiting + lockout persisted in its own
        // DbContext scope, so it never rides the caller's transaction/idempotency).
        _pinVerifier.VerifyPinAsync(userId, pin);

    /// <inheritdoc />
    public async Task SetPinAsync(Guid userId, SetPinRequest request)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());

        if (user == null)
        {
            throw new NotFoundException("User", userId);
        }

        user.PinHash = _passwordHasher.HashPin(request.Pin);
        var result = await _userManager.UpdateAsync(user);

        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new BusinessRuleException($"Failed to set PIN: {errors}");
        }

        _logger.LogInformation("User {UserId} set their PIN", userId);
    }
}
