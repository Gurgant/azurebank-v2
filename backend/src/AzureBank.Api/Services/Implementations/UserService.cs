using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.User;
using AzureBank.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// User service handling profile and recipient lookup operations.
/// </summary>
public class UserService : IUserService
{
    private readonly AzureBankDbContext _context;
    private readonly UserMapper _mapper;
    private readonly ILogger<UserService> _logger;

    public UserService(AzureBankDbContext context, ILogger<UserService> logger)
    {
        _context = context;
        _mapper = new UserMapper();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<UserResponse> GetUserByIdAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);

        if (user == null)
        {
            throw new NotFoundException("User", userId);
        }

        return _mapper.ToResponse(user);
    }

    /// <inheritdoc />
    public async Task<RecipientLookupResponse> GetUserByAzureTagAsync(string azureTag, Guid currentUserId)
    {
        // AzureTags are stored lower-cased; normalise the same way (invariant, not the
        // current culture — a Turkish-I difference would silently mismatch).
        var normalizedTag = azureTag.ToLowerInvariant();

        // EXACT match only — no substring/prefix search (ADR-0014). Project to just the
        // fields the response needs so the query never materialises the full ApplicationUser
        // (PasswordHash / PinHash / SecurityStamp) into memory.
        var match = await _context.Users
            .Where(u => u.AzureTag == normalizedTag)
            .Select(u => new { u.Id, u.FirstName, u.LastName })
            .FirstOrDefaultAsync();

        // Looking up yourself is not a valid transfer recipient — report not-found without
        // echoing a name (the transfer endpoint blocks self-transfer separately).
        if (match is not null && match.Id == currentUserId)
        {
            return new RecipientLookupResponse { AzureTag = azureTag, DisplayName = string.Empty, Exists = false };
        }

        return new RecipientLookupResponse
        {
            AzureTag = azureTag,
            DisplayName = match is not null ? MaskDisplayName(match.FirstName, match.LastName) : string.Empty,
            Exists = match is not null
        };
    }

    // "Vladislav A." — enough to confirm the right payee, not the full surname (ADR-0014).
    // Trim for display robustness only: the name charset permits edge spaces and registration
    // persists names untrimmed, so a surname like " Smith" would otherwise mask to "John  ."
    // (This is presentation normalisation, not input validation.)
    private static string MaskDisplayName(string firstName, string? lastName)
    {
        var first = firstName.Trim();
        return string.IsNullOrWhiteSpace(lastName) ? first : $"{first} {lastName.Trim()[0]}.";
    }
}
