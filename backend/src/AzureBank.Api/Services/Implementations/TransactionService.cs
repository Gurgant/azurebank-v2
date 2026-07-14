using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Utilities;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// Transaction service handling deposits, withdrawals, and transaction history.
/// </summary>
public class TransactionService : ITransactionService
{
    private readonly AzureBankDbContext _context;
    private readonly IAccountAccessService _accountAccess;
    private readonly IPinVerifier _pinVerifier;
    private readonly TransactionMapper _mapper;
    private readonly ILogger<TransactionService> _logger;

    public TransactionService(
        AzureBankDbContext context,
        IAccountAccessService accountAccess,
        IPinVerifier pinVerifier,
        TransactionMapper mapper,
        ILogger<TransactionService> logger)
    {
        _context = context;
        _accountAccess = accountAccess;
        _pinVerifier = pinVerifier;
        _mapper = mapper;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DepositResponse> DepositAsync(Guid userId, DepositRequest request)
    {
        var account = await _accountAccess.GetAccountWithOwnershipCheckAsync(request.AccountId, userId);

        // Optimistic-concurrency retry: a parallel operation on the SAME
        // account bumps its RowVersion between our read and commit; the
        // loser reloads and recomputes instead of surfacing a 500.
        for (var attempt = 1; ; attempt++)
        {
            var balanceBefore = account.Balance;
            var balanceAfter = balanceBefore + request.Amount;

            var transaction = new Transaction
            {
                Id = Guid.CreateVersion7(),
                TransactionNumber = IdGenerator.GenerateTransactionNumber(),
                AccountId = account.Id,
                Account = account,
                Type = TransactionType.Deposit,
                Amount = request.Amount,
                BalanceBefore = balanceBefore,
                BalanceAfter = balanceAfter,
                Description = request.Description,
                Status = TransactionStatus.Completed,
                CreatedAt = DateTime.UtcNow
            };

            // Update account balance
            account.Balance = balanceAfter;
            account.UpdatedAt = DateTime.UtcNow;

            _context.Transactions.Add(transaction);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException ex) when (ConcurrencyRetry.ShouldRetry(ex, attempt))
            {
                _logger.LogInformation(
                    "Concurrency conflict on deposit to account {AccountId} (attempt {Attempt}); retrying",
                    account.Id, attempt);
                await ConcurrencyRetry.PrepareNextAttemptAsync(_context, account);
                continue;
            }

            _logger.LogInformation(
                "Deposit of {Amount:C} to account {AccountId}. New balance: {Balance:C}",
                request.Amount, account.Id, balanceAfter);

            return _mapper.ToDepositResponse(transaction, balanceAfter);
        }
    }

    /// <inheritdoc />
    public async Task<WithdrawResponse> WithdrawAsync(Guid userId, WithdrawRequest request)
    {
        var account = await _accountAccess.GetAccountWithOwnershipCheckAsync(request.AccountId, userId);

        // Verify PIN for withdrawal (step-up authentication)
        var user = await _context.Users.FindAsync(userId);
        if (user == null || string.IsNullOrEmpty(user.PinHash))
        {
            throw new BusinessRuleException("PIN must be set before making withdrawals.", ErrorCodes.PinRequired);
        }

        // Verify the PIN with attempt-limiting: throws 429 PIN_LOCKED if the PIN
        // is locked (before any money moves), otherwise 401 on a wrong PIN.
        if (!await _pinVerifier.VerifyPinAsync(userId, request.Pin))
        {
            throw new AuthenticationException("Invalid PIN.", ErrorCodes.InvalidPin);
        }

        // Optimistic-concurrency retry: see DepositAsync. The funds check
        // runs INSIDE the loop — a reloaded balance may no longer cover the
        // withdrawal.
        for (var attempt = 1; ; attempt++)
        {
            // Check sufficient funds
            if (account.Balance < request.Amount)
            {
                throw new InsufficientFundsException(account.Balance, request.Amount);
            }

            var balanceBefore = account.Balance;
            var balanceAfter = balanceBefore - request.Amount;

            var transaction = new Transaction
            {
                Id = Guid.CreateVersion7(),
                TransactionNumber = IdGenerator.GenerateTransactionNumber(),
                AccountId = account.Id,
                Account = account,
                Type = TransactionType.Withdrawal,
                Amount = request.Amount,
                BalanceBefore = balanceBefore,
                BalanceAfter = balanceAfter,
                Description = request.Description,
                Status = TransactionStatus.Completed,
                CreatedAt = DateTime.UtcNow
            };

            // Update account balance
            account.Balance = balanceAfter;
            account.UpdatedAt = DateTime.UtcNow;

            _context.Transactions.Add(transaction);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException ex) when (ConcurrencyRetry.ShouldRetry(ex, attempt))
            {
                _logger.LogInformation(
                    "Concurrency conflict on withdrawal from account {AccountId} (attempt {Attempt}); retrying",
                    account.Id, attempt);
                await ConcurrencyRetry.PrepareNextAttemptAsync(_context, account);
                continue;
            }

            _logger.LogInformation(
                "Withdrawal of {Amount:C} from account {AccountId}. New balance: {Balance:C}",
                request.Amount, account.Id, balanceAfter);

            return _mapper.ToWithdrawResponse(transaction, balanceAfter);
        }
    }

    /// <inheritdoc />
    public async Task<PaginatedResponse<TransactionResponse>> GetTransactionsAsync(Guid userId, TransactionFilter filter)
    {
        // Get user's account IDs for filtering
        var userAccountIds = await _context.Accounts
            .AsNoTracking()
            .Where(a => a.UserId == userId && !a.IsDeleted)
            .Select(a => a.Id)
            .ToListAsync();

        if (!userAccountIds.Any())
        {
            return new PaginatedResponse<TransactionResponse>
            {
                Data = [],
                Pagination = new PaginationMetadata
                {
                    Page = filter.Page,
                    PageSize = filter.PageSize,
                    TotalItems = 0,
                    TotalPages = 0
                }
            };
        }

        // Build query (AsNoTracking for read-only)
        var query = _context.Transactions
            .AsNoTracking()
            .Where(t => userAccountIds.Contains(t.AccountId));

        // Filter by specific account if provided
        if (filter.AccountId.HasValue)
        {
            if (!userAccountIds.Contains(filter.AccountId.Value))
            {
                throw new AuthorizationException("You do not have access to this account.");
            }
            query = query.Where(t => t.AccountId == filter.AccountId.Value);
        }

        // Filter by date range
        if (filter.FromDate.HasValue)
        {
            query = query.Where(t => t.CreatedAt >= filter.FromDate.Value);
        }

        if (filter.ToDate.HasValue)
        {
            query = query.Where(t => t.CreatedAt <= filter.ToDate.Value);
        }

        // Get total count
        var totalItems = await query.CountAsync();

        // Order and paginate
        var transactions = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)totalItems / filter.PageSize);

        return new PaginatedResponse<TransactionResponse>
        {
            Data = _mapper.ToResponseList(transactions),
            Pagination = new PaginationMetadata
            {
                Page = filter.Page,
                PageSize = filter.PageSize,
                TotalItems = totalItems,
                TotalPages = totalPages
            }
        };
    }

    /// <inheritdoc />
    public async Task<TransactionResponse> GetTransactionByIdAsync(Guid transactionId, Guid userId)
    {
        var transaction = await _context.Transactions
            .AsNoTracking()
            .Include(t => t.Account)
            .FirstOrDefaultAsync(t => t.Id == transactionId);

        if (transaction == null)
        {
            throw new NotFoundException("Transaction", transactionId);
        }

        // Verify ownership
        if (transaction.Account.UserId != userId)
        {
            throw new AuthorizationException("You do not have access to this transaction.");
        }

        return _mapper.ToResponse(transaction);
    }
}
