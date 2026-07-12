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
    private readonly UserMapper _userMapper;
    private readonly AccountMapper _accountMapper;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        AzureBankDbContext context,
        IJwtService jwtService,
        IPasswordHasher passwordHasher,
        UserMapper userMapper,
        AccountMapper accountMapper,
        ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _context = context;
        _jwtService = jwtService;
        _passwordHasher = passwordHasher;
        _userMapper = userMapper;
        _accountMapper = accountMapper;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);

        if (user == null || !await _userManager.CheckPasswordAsync(user, request.Password))
        {
            // Prevent user enumeration - same message for both cases
            _logger.LogWarning("Failed login attempt for email {Email}", request.Email);
            throw new AuthenticationException("Invalid email or password.");
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
    public async Task<bool> VerifyPinAsync(Guid userId, string pin)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());

        if (user == null)
        {
            _logger.LogWarning("PIN verification attempted for non-existent user {UserId}", userId);
            return false;
        }

        if (string.IsNullOrEmpty(user.PinHash))
        {
            _logger.LogWarning("PIN verification attempted for user {UserId} without PIN set", userId);
            return false;
        }

        var isValid = _passwordHasher.VerifyPin(user.PinHash, pin);

        if (isValid)
        {
            _logger.LogInformation("PIN verified successfully for user {UserId}", userId);
        }
        else
        {
            _logger.LogWarning("Invalid PIN attempt for user {UserId}", userId);
        }

        return isValid;
    }

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
