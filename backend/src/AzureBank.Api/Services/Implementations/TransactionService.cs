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
    private readonly IAuditService _audit;

    public TransactionService(
        AzureBankDbContext context,
        IAccountAccessService accountAccess,
        IPinVerifier pinVerifier,
        TransactionMapper mapper,
        ILogger<TransactionService> logger,
        IAuditService audit)
    {
        _audit = audit;
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

            /*
              THE AUDIT ROW RIDES THIS SAVE (ADR-0044 D1), and it sits INSIDE the retry loop because
              the transaction Id is minted inside it too — the row's subject is that id, so it cannot
              be written before one exists. A failed attempt's row is detached by
              ConcurrencyRetry.ResetToStoreAsync alongside the ledger rows it describes, so a deposit
              that takes three attempts still commits exactly ONE row rather than three claiming
              three deposits.

              Detail stays null on every money event. The amount, the description and the account are
              already on the ledger row SubjectId reaches; copying them into a table designed never to
              be purged is precisely how D5 gets broken, and an amount tied to an actor id is
              financial data about an identifiable person.
            */
            _audit.Record(
                SecurityEvents.MoneyDeposited, AuditOutcome.Succeeded,
                actorUserId: userId, subjectType: "Transaction", subjectId: transaction.Id);

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
                    SecurityEvents.TransactionNumberCollision, account.Id, attempt);
                await ConcurrencyRetry.PrepareNextAttemptAsync(_context, account);
                continue;
            }

            _logger.LogInformation(
                "Deposit of {Amount} to account {AccountId}. New balance: {Balance}",
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

        /*
          RECORD-AND-RETHROW, and the try exists for a refusal that never returns. VerifyPinAsync
          THROWS PinLockedException rather than returning false when the attempt limit has been
          reached, so the lockout -- the control standing between a guessed PIN and a balance --
          cannot be observed by inspecting the return value. Measured: PinService throws at two
          places and audits at neither, so before this change a tripped lockout left the trail
          completely silent.

          RecordRefusalAsync, never Record: every branch here throws, so a row enlisted in the
          caller's unit of work would be rolled back by the very refusal it documents.
        */
        bool pinOk;
        try
        {
            // Verify the PIN with attempt-limiting: throws 429 PIN_LOCKED if the PIN
            // is locked (before any money moves), otherwise 401 on a wrong PIN.
            pinOk = await _pinVerifier.VerifyPinAsync(userId, request.Pin);
        }
        catch (PinLockedException)
        {
            await _audit.RecordRefusalAsync(
                SecurityEvents.MoneyWithdrawalRefused, AuditOutcome.Refused,
                actorUserId: userId, subjectType: "Account", subjectId: account.Id,
                detail: ErrorCodes.PinLocked);
            throw;
        }

        if (!pinOk)
        {
            await _audit.RecordRefusalAsync(
                SecurityEvents.MoneyWithdrawalRefused, AuditOutcome.Refused,
                actorUserId: userId, subjectType: "Account", subjectId: account.Id,
                detail: ErrorCodes.InvalidPin);
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
                /*
                  NO AUDIT ROW HERE, and it is a correction to the first version of this change.
                  ADR-0044 had already classified insufficient funds as a routine user outcome whose
                  row-per-attempt is an unbounded write into a never-purged table, and that decision
                  was reasoned before this branch existed. Wiring it anyway would have contradicted
                  the ADR without arguing with it.

                  ⚠️ AND THE CONTENTION ANGLE IS SHARPER THAN THE ADR STATED. A wrong PIN is BOUNDED
                  -- three attempts and ADR-0010 locks the PIN. This is not: a caller can ask to
                  withdraw more than they hold forever, at no cost, and every attempt would take the
                  chain tail lock that every real money movement queues behind. An unaudited routine
                  refusal is a gap; an audited one here is a contention amplifier anybody can drive.
                */
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

            // Same placement and the same reasoning as the deposit above: inside the loop because
            // the id is minted there, detached with the attempt if the attempt fails.
            _audit.Record(
                SecurityEvents.MoneyWithdrawn, AuditOutcome.Succeeded,
                actorUserId: userId, subjectType: "Transaction", subjectId: transaction.Id);

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
                    SecurityEvents.TransactionNumberCollision, account.Id, attempt);
                await ConcurrencyRetry.PrepareNextAttemptAsync(_context, account);
                continue;
            }

            _logger.LogInformation(
                "Withdrawal of {Amount} from account {AccountId}. New balance: {Balance}",
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

        /*
          Order and paginate. The Id tiebreaker is NOT decoration: CreatedAt is stamped once per
          SaveChanges, so every transfer's two legs — and any two rows written together — carry the
          SAME instant. Ordering on CreatedAt alone leaves those tied, and a tied ORDER BY under
          OFFSET/FETCH is free to return a row on two pages and skip another, because the order
          between ties is a plan and storage-layout artefact rather than something we asked for.

          What the tiebreaker buys is a TOTAL, STABLE order — that is the property paging needs, and
          the only one claimed here. It is deliberately NOT claimed that a tie then reads
          chronologically: Guid.CreateVersion7 seeds rand_a/rand_b with random data rather than a
          counter, so two ids minted in the same millisecond sort arbitrarily; and SQL Server orders
          uniqueidentifier on a byte order of its own, which is not Guid.CompareTo's. Across
          milliseconds UUIDv7 does read chronologically — but two rows in one SaveChanges are exactly
          the case where it may not, so nothing here depends on it.
        */
        var transactions = await query
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
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
