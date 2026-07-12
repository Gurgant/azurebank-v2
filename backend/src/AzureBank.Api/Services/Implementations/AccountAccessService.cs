using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// Service for retrieving accounts with ownership verification.
/// Centralizes ownership check logic to eliminate duplication across services.
/// </summary>
public class AccountAccessService : IAccountAccessService
{
    private readonly AzureBankDbContext _context;

    public AccountAccessService(AzureBankDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<Account> GetAccountWithOwnershipCheckAsync(Guid accountId, Guid userId)
    {
        var account = await _context.Accounts
            .FirstOrDefaultAsync(a => a.Id == accountId && !a.IsDeleted);

        if (account == null)
        {
            throw new NotFoundException("Account", accountId);
        }

        if (account.UserId != userId)
        {
            throw new AuthorizationException("You do not have access to this account.");
        }

        return account;
    }

    /// <inheritdoc />
    public async Task<bool> ValidateAccountOwnershipAsync(Guid accountId, Guid userId)
    {
        return await _context.Accounts
            .AnyAsync(a => a.Id == accountId && a.UserId == userId && !a.IsDeleted);
    }
}
