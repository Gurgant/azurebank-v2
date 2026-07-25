using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Utilities;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// Account management service handling CRUD operations and balance queries.
/// </summary>
public class AccountService : IAccountService
{
    private readonly AzureBankDbContext _context;
    private readonly IAccountAccessService _accountAccess;
    private readonly AccountMapper _mapper;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        AzureBankDbContext context,
        IAccountAccessService accountAccess,
        AccountMapper mapper,
        ILogger<AccountService> logger)
    {
        _context = context;
        _accountAccess = accountAccess;
        _mapper = mapper;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<AccountResponse>> GetUserAccountsAsync(Guid userId)
    {
        var accounts = await _context.Accounts
            .AsNoTracking()
            .Where(a => a.UserId == userId && !a.IsDeleted)
            .OrderByDescending(a => a.IsPrimary)
            .ThenBy(a => a.CreatedAt)
            .ToListAsync();

        return _mapper.ToResponseList(accounts);
    }

    /// <inheritdoc />
    public async Task<AccountResponse> GetAccountByIdAsync(Guid accountId, Guid userId)
    {
        var account = await _accountAccess.GetAccountWithOwnershipCheckAsync(accountId, userId);
        return _mapper.ToResponse(account);
    }

    /// <inheritdoc />
    public async Task<AccountResponse> CreateAccountAsync(Guid userId, CreateAccountRequest request)
    {
        var account = new Account
        {
            UserId = userId,
            AccountNumber = IdGenerator.GenerateAccountNumber(),
            Name = request.Name,
            Type = request.Type,
            Balance = 0,
            IsPrimary = false, // Only first account is primary, set via SetPrimaryAccountAsync
            User = null! // EF Core manages navigation via UserId
        };

        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Created account {AccountId} for user {UserId}", account.Id, userId);

        return _mapper.ToResponse(account);
    }

    /// <inheritdoc />
    public async Task<AccountResponse> UpdateAccountAsync(Guid accountId, Guid userId, UpdateAccountRequest request)
    {
        var account = await _accountAccess.GetAccountWithOwnershipCheckAsync(accountId, userId);

        account.Name = request.Name;
        account.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        // Sanitize the user-controlled name before logging — defence-in-depth against
        // log-forging into the plain-text sink (the structured template already mitigates most).
        // Central LogSanitizer (not inline Replace): one audited contract, pinned by tests and
        // declared to CodeQL as a log-injection barrier (see the model pack under .github/codeql).
        var safeName = LogSanitizer.Sanitize(request.Name);
        _logger.LogInformation("Updated account {AccountId} name to '{Name}'", accountId, safeName);

        return _mapper.ToResponse(account);
    }

    /// <inheritdoc />
    public async Task SetPrimaryAccountAsync(Guid userId, Guid accountId)
    {
        // Verify the account exists and belongs to user
        var account = await _accountAccess.GetAccountWithOwnershipCheckAsync(accountId, userId);

        // Get current primary account (if any)
        var currentPrimary = await _context.Accounts
            .FirstOrDefaultAsync(a => a.UserId == userId && a.IsPrimary && !a.IsDeleted);

        // Unset current primary
        if (currentPrimary != null && currentPrimary.Id != accountId)
        {
            currentPrimary.IsPrimary = false;
            currentPrimary.UpdatedAt = DateTime.UtcNow;
        }

        // Set new primary
        account.IsPrimary = true;
        account.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Set account {AccountId} as primary for user {UserId}", accountId, userId);
    }

    /// <inheritdoc />
    public async Task DeleteAccountAsync(Guid accountId, Guid userId)
    {
        var account = await _accountAccess.GetAccountWithOwnershipCheckAsync(accountId, userId);

        // Business rules: cannot delete if balance is non-zero
        if (account.Balance != 0)
        {
            throw new BusinessRuleException(
                $"Cannot delete account with non-zero balance. Current balance: {account.Balance:C}",
                "NON_ZERO_BALANCE");
        }

        // Business rules: cannot delete primary account
        if (account.IsPrimary)
        {
            throw new BusinessRuleException(
                "Cannot delete primary account. Set another account as primary first.",
                "PRIMARY_ACCOUNT_DELETE");
        }

        // Soft delete
        account.IsDeleted = true;
        account.DeletedAt = DateTime.UtcNow;
        account.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Soft deleted account {AccountId}", accountId);
    }

    /// <inheritdoc />
    public async Task<BalanceResponse> GetBalanceAsync(Guid accountId, Guid userId, DateTime? atTime = null)
    {
        var account = await _accountAccess.GetAccountWithOwnershipCheckAsync(accountId, userId);

        if (atTime == null || atTime >= DateTime.UtcNow)
        {
            // Return current balance
            return _mapper.ToBalanceResponse(account, DateTime.UtcNow, isHistorical: false);
        }

        // Calculate historical balance by summing transactions
        var historicalBalance = await CalculateHistoricalBalanceAsync(accountId, atTime.Value);

        return _mapper.ToHistoricalBalanceResponse(accountId, historicalBalance, atTime.Value);
    }

    /// <inheritdoc />
    public async Task<AccountNumberResponse> GetFullAccountNumberAsync(Guid accountId, Guid userId)
    {
        var account = await _accountAccess.GetAccountWithOwnershipCheckAsync(accountId, userId);

        // Detective audit line (SecurityEvent series): WHO revealed WHICH account —
        // never the number itself. PII redaction is opt-in per call site, so the value
        // must not enter the logging pipeline at all.
        _logger.LogInformation(
            "SecurityEvent {SecurityEvent}: user {UserId} revealed the full account number of account {AccountId}",
            "AccountNumberRevealed", userId, accountId);

        // Deliberately NOT via AccountMapper: the mapper's contract is "account numbers
        // leave masked". Constructing the one unmasked shape by hand keeps that invariant
        // and prevents any generated mapping from ever adopting the raw value.
        return new AccountNumberResponse
        {
            AccountId = account.Id,
            AccountNumber = account.AccountNumber
        };
    }

    /// <summary>
    /// Calculates the account balance at a specific point in time.
    /// Works by getting all transactions up to that time and calculating the final balance.
    /// </summary>
    private async Task<decimal> CalculateHistoricalBalanceAsync(Guid accountId, DateTime atTime)
    {
        // Get the most recent transaction before or at the specified time
        var lastTransaction = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.AccountId == accountId && t.CreatedAt <= atTime)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (lastTransaction == null)
        {
            // No transactions at that time - balance was 0
            return 0;
        }

        return lastTransaction.BalanceAfter;
    }
}
