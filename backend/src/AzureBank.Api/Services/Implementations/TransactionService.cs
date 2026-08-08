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
            catch (DbUpdateException ex) when (ConcurrencyRetry.IsTransactionNumberCollision(ex, attempt))
            {
                // A regenerable clash on the transaction number: the next attempt mints a fresh
                // one. Why it is safe to retry, and why it is narrowed by INDEX NAME rather than by
                // error number, lives on ConcurrencyRetry.IsTransactionNumberCollision — one
                // authoritative copy instead of four that drift. Warning, not Information: it
                // should never happen, so an occurrence means the entropy assumption deserves
                // re-checking, which needs to know WHICH account.
                _logger.LogWarning(
                    ex,
                    "SecurityEvent {SecurityEvent}: transaction-number collision on deposit to "
                        + "account {AccountId} (attempt {Attempt}); regenerating",
                    "TransactionNumberCollision", account.Id, attempt);
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
            catch (DbUpdateException ex) when (ConcurrencyRetry.IsTransactionNumberCollision(ex, attempt))
            {
                // A regenerable clash on the transaction number: the next attempt mints a fresh
                // one. Why it is safe to retry, and why it is narrowed by INDEX NAME rather than by
                // error number, lives on ConcurrencyRetry.IsTransactionNumberCollision — one
                // authoritative copy instead of four that drift. Warning, not Information: it
                // should never happen, so an occurrence means the entropy assumption deserves
                // re-checking, which needs to know WHICH account.
                _logger.LogWarning(
                    ex,
                    "SecurityEvent {SecurityEvent}: transaction-number collision on withdrawal "
                        + "from account {AccountId} (attempt {Attempt}); regenerating",
                    "TransactionNumberCollision", account.Id, attempt);
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
    public async Task<TransactionSummaryResponse> GetSummaryAsync(Guid userId, TransactionSummaryFilter filter)
    {
        // Resolve the window: default = the current UTC calendar month so far.
        var now = DateTime.UtcNow;
        var from = filter.FromDate ?? new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = filter.ToDate ?? now;

        // Guard the RESOLVED window too — the filter's model validation only sees the
        // explicitly-provided pair (a lone future FromDate lands here, not there).
        if (from > to)
        {
            throw new BusinessRuleException(
                "FromDate must be earlier than or equal to ToDate.", ErrorCodes.InvalidDateRange);
        }

        // A caller-supplied account has to be proved OWNED before it can narrow anything, and the
        // check is deliberately shaped differently from GetTransactionsAsync's.
        //
        // That one materializes every account id the user has and tests membership in memory,
        // because it needs the list anyway to bound the query. Here the aggregate already scopes
        // itself through the Account navigation, so the same approach would add a second query and
        // an unbounded IN list to EVERY call — including the all-accounts default, which is the
        // common one. A single bounded existence check costs one extra round trip only when the
        // caller actually asks for a scope, and the default path stays exactly one query.
        //
        // The predicate mirrors the list's membership test term for term — same user, same
        // `!IsDeleted` — so a soft-deleted account is a 403 in both places rather than an empty
        // summary in one and a refusal in the other.
        if (filter.AccountId.HasValue)
        {
            var owned = await _context.Accounts
                .AsNoTracking()
                .AnyAsync(a => a.Id == filter.AccountId.Value
                    && a.UserId == userId
                    && !a.IsDeleted);

            if (!owned)
            {
                // The SAME refusal whether the account belongs to someone else or does not exist
                // at all. Distinguishing them would turn this endpoint into an oracle for guessing
                // account ids, and the message is the list's, verbatim, for the same reason.
                throw new AuthorizationException("You do not have access to this account.");
            }
        }

        var summary = new TransactionSummaryResponse { FromDate = from, ToDate = to };

        // ONE round trip for the aggregate: ownership scoping rides it via the Account navigation
        // (JOIN on the FK). A user with no accounts simply aggregates zero rows (null totals →
        // zero-valued summary), and so does an owned account with nothing in the window.
        // Only Completed transactions count toward money totals: Pending/Failed/Reversed
        // must not inflate income or expenses; the conditional aggregates translate to
        // SUM(CASE WHEN …).
        var totals = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.Account.UserId == userId
                && !t.Account.IsDeleted
                && (filter.AccountId == null || t.AccountId == filter.AccountId)
                && t.CreatedAt >= from
                && t.CreatedAt <= to)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Income = g
                    .Where(t => t.Status == TransactionStatus.Completed
                        && (t.Type == TransactionType.Deposit || t.Type == TransactionType.TransferIn))
                    .Sum(t => (decimal?)t.Amount) ?? 0m,
                Expenses = g
                    .Where(t => t.Status == TransactionStatus.Completed
                        && (t.Type == TransactionType.Withdrawal || t.Type == TransactionType.TransferOut))
                    .Sum(t => (decimal?)t.Amount) ?? 0m,
                Pending = g.Count(t => t.Status == TransactionStatus.Pending)
            })
            .FirstOrDefaultAsync();

        if (totals != null)
        {
            summary.TotalIncome = totals.Income;
            summary.TotalExpenses = totals.Expenses;
            summary.NetChange = totals.Income - totals.Expenses;
            summary.PendingCount = totals.Pending;
        }

        return summary;
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
