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
        var normalizedTag = azureTag.ToLower();

        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.AzureTag == normalizedTag);

        // If looking up self, return exists but indicate it's self
        if (user != null && user.Id == currentUserId)
        {
            return new RecipientLookupResponse
            {
                AzureTag = azureTag,
                DisplayName = "This is your own account",
                Exists = false // Treat as not found for transfer purposes
            };
        }

        return _mapper.ToLookupResponse(user, azureTag);
    }

    /// <inheritdoc />
    public async Task<List<RecipientSearchResult>> SearchUsersAsync(string azureTagQuery, Guid excludeUserId)
    {
        if (string.IsNullOrEmpty(azureTagQuery) || azureTagQuery.Length < 2)
        {
            return [];
        }

        var normalizedQuery = azureTagQuery.ToLower();

        var users = await _context.Users
            .Where(u => u.Id != excludeUserId && u.AzureTag.Contains(normalizedQuery))
            .Take(10)
            .ToListAsync();

        return _mapper.ToSearchResultList(users);
    }
}
