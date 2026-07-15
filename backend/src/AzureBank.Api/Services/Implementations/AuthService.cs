using AzureBank.Api.Mappers;
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
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        AzureBankDbContext context,
        IJwtService jwtService,
        IPasswordHasher passwordHasher,
        IPinVerifier pinVerifier,
        UserMapper userMapper,
        AccountMapper accountMapper,
        ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _context = context;
        _jwtService = jwtService;
        _passwordHasher = passwordHasher;
        _pinVerifier = pinVerifier;
        _userMapper = userMapper;
        _accountMapper = accountMapper;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // Unknown user: generic failure, identical to a wrong password (no enumeration).
            _logger.LogWarning("Failed login attempt for email {Email}", request.Email);
            throw new AuthenticationException("Invalid email or password.");
        }

        var now = DateTimeOffset.UtcNow;
        var passwordOk = await _userManager.CheckPasswordAsync(user, request.Password);
        var lockedUntil = user.LockoutEnd is { } end && end > now ? end : (DateTimeOffset?)null;

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

            var token = _jwtService.GenerateToken(user);
            _logger.LogInformation("User {UserId} logged in successfully", user.Id);
            return new LoginResponse
            {
                Token = token,
                ExpiresAt = DateTime.UtcNow.AddMinutes(15), // Should come from JWT options
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
        _logger.LogWarning("Failed login attempt for email {Email}", request.Email);
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
        var max = ValidationRules.MaxLoginAttempts;
        var until = now.AddMinutes(ValidationRules.LoginLockoutMinutes);

        if (_context.Database.IsRelational())
        {
            await _context.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.AccessFailedCount,
                        u => u.AccessFailedCount + 1 >= max ? 0 : u.AccessFailedCount + 1)
                    .SetProperty(u => u.LockoutEnd,
                        u => u.AccessFailedCount + 1 >= max
                            ? (DateTimeOffset?)until
                            : (u.LockoutEnd != null && u.LockoutEnd < now ? (DateTimeOffset?)null : u.LockoutEnd)));
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
        await _context.SaveChangesAsync();
    }

    private async Task ResetLoginLockoutAsync(ApplicationUser user)
    {
        if (_context.Database.IsRelational())
        {
            await _context.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.AccessFailedCount, 0)
                    .SetProperty(u => u.LockoutEnd, (DateTimeOffset?)null));
            return;
        }

        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<RegisterResponse> RegisterAsync(RegisterRequest request)
    {
        // Check for existing email
        if (await _userManager.FindByEmailAsync(request.Email) != null)
        {
            throw new ConflictException("Email is already registered.", "DUPLICATE_EMAIL");
        }

        // Check for existing AzureTag
        var normalizedAzureTag = request.AzureTag.ToLower();
        if (await _context.Users.AnyAsync(u => u.AzureTag == normalizedAzureTag))
        {
            throw new ConflictException("AzureTag is already taken.", "DUPLICATE_AZURE_TAG");
        }

        var user = new ApplicationUser
        {
            UserName = normalizedAzureTag,
            Email = request.Email,
            AzureTag = normalizedAzureTag,
            FirstName = request.FirstName,
            LastName = request.LastName,
            EmailConfirmed = true // Skip email verification for MVP
        };

        var result = await _userManager.CreateAsync(user, request.Password);

        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            _logger.LogWarning("User registration failed: {Errors}", errors);
            throw new BusinessRuleException($"Registration failed: {errors}");
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

        var token = _jwtService.GenerateToken(user);

        _logger.LogInformation("User {UserId} registered successfully with account {AccountId}", user.Id, account.Id);

        return new RegisterResponse
        {
            User = _userMapper.ToLoginInfo(user),
            Account = _accountMapper.ToResponse(account),
            Token = new Shared.DTOs.Auth.TokenResponse
            {
                AccessToken = token,
                ExpiresIn = 15 * 60, // 15 minutes in seconds
                TokenType = "Bearer",
                ExpiresAt = DateTime.UtcNow.AddMinutes(15)
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
