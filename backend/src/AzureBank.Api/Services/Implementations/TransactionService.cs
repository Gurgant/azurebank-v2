using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
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
    private readonly IStepUpAuthorizationService _stepUp;
    private readonly TransactionMapper _mapper;
    private readonly ILogger<TransactionService> _logger;
    private readonly IAuditService _audit;

    public TransactionService(
        AzureBankDbContext context,
        IAccountAccessService accountAccess,
        IStepUpAuthorizationService stepUp,
        TransactionMapper mapper,
        ILogger<TransactionService> logger,
        IAuditService audit)
    {
        _audit = audit;
        _context = context;
        _accountAccess = accountAccess;
        _stepUp = stepUp;
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

              Detail stays null on every money event that consumes no authorisation (since
              2026-09-14 the two transfers name theirs: AuditDetails). The amount, the description
              and the account are already on the ledger row SubjectId reaches; copying them into a
              table designed never to be purged is precisely how D5 gets broken, and an amount tied
              to an actor id is financial data about an identifiable person.
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

            // No amount and no balance: logs are exported, money is financial data, and the
            // transaction number is the key to both (ADR-0017 D5). This line carried both until
            // 2026-09-11, while the ADR said it did not; LogPlaceholderClassTests now keeps it out.
            _logger.LogInformation(
                "Deposit to account {AccountId}. Transaction: {TransactionNumber}",
                account.Id, transaction.TransactionNumber);

            return _mapper.ToDepositResponse(transaction, balanceAfter);
        }
    }

    /// <inheritdoc />
    public async Task<StepUpAuthorizationResponse> AuthoriseWithdrawalAsync(
        Guid userId, WithdrawalAuthorizationRequest request)
    {
        /*
          OWNERSHIP FIRST, exactly as the transfer mints and the closure mint do: an unknown or
          foreign account is a 404/403 BEFORE the PIN is consulted, so probing someone else's
          account costs no PIN attempt. Getting this order wrong would make this endpoint a
          cheaper oracle than the withdrawal itself, which is the whole thing the rail prevents.
        */
        var account = await _accountAccess.GetAccountWithOwnershipCheckAsync(request.AccountId, userId);

        /*
          AND NO FUNDS CHECK HERE, deliberately (ADR-0050 D4, followed by ADR-0056).

          The transfer mints do not lift the balance guard to mint time either, and the reason is
          that a mint is an AUTHENTICATION event, not a decision about whether the money can move.
          Balance is a racing value: one checked here would be re-checked at the withdrawal anyway,
          and refusing at the mint would only teach a caller -- at the cost of nothing -- what the
          balance is, while adding a second place for the two checks to disagree.

          Consequence, stated because it is a behaviour change and not an oversight: asking to
          withdraw more than the account holds still mints a 201. The 422 arrives at the withdrawal,
          where the balance is read inside the transaction that spends it.
        */
        var authorization = await _stepUp.MintAsync(
            userId,
            StepUpOperation.Withdrawal,
            StepUpBinding.ForWithdrawal(account.Id, request.Amount),
            request.Pin);

        return new StepUpAuthorizationResponse
        {
            AuthorizationId = authorization.Id,
            ExpiresAt = authorization.ExpiresAt
        };
    }

    /// <inheritdoc />
    public async Task<WithdrawResponse> WithdrawAsync(
        Guid userId, WithdrawRequest request, Guid? stepUpAuthorizationId)
    {
        var account = await _accountAccess.GetAccountWithOwnershipCheckAsync(request.AccountId, userId);

        /*
          THE FUNDS GUARD RUNS BEFORE THE AUTHORISATION IS EVEN LOOKED AT, and that ordering is the
          user-visible half of ADR-0056 (D4).

          Before the rail, the PIN was proved first and the balance second, so a customer who
          mistyped their PIN on a withdrawal they could never afford spent one of their three
          attempts on it -- and three of those locked the PIN (ADR-0010). The refusal they deserved
          was "you do not have that much", and the one they got was "wrong PIN", followed by a lock.
          Now the affordability answer comes first and costs nothing, and the PIN is only ever
          consulted at the mint, for a withdrawal that could actually happen.

          NO AUDIT ROW HERE, and it is the decision ADR-0044 already reasoned, kept verbatim through
          the restructure. Insufficient funds is a routine user outcome whose row-per-attempt is an
          unbounded write into a never-purged table. The contention angle is sharper still: a wrong
          PIN is BOUNDED -- three attempts and the PIN locks -- while this is not. A caller can ask
          to withdraw more than they hold forever, at no cost, and every attempt would take the
          chain tail lock that every real money movement queues behind. An unaudited routine refusal
          is a gap; an audited one here is a contention amplifier anybody can drive.
        */
        if (account.Balance < request.Amount)
        {
            throw new InsufficientFundsException(account.Balance, request.Amount);
        }

        /*
          RECORDED AT THE CALL SITE, after ownership and after the funds guard, so the refusal names
          an account the actor owns and an amount they could have withdrawn. On its OWN connection
          (RecordRefusalAsync, never Record), because this request writes nothing else: there is no
          transaction for the row to ride, and the throw below is the whole outcome -- a row
          enlisted in a unit of work would be rolled back by the very refusal it documents.

          An EMPTY Step-Up-Authorization header binds to null exactly like an absent one, so both
          land here and both get 401 AUTHORIZATION_REQUIRED. A header that is present but not a UUID
          never reaches this method: MVC model binding refuses it upstream with a 400 keyed on the
          header name, with no errorCode -- measured on the transfer endpoints (ADR-0042) and on the
          closure (ADR-0049), and the same binder serves this one.
        */
        if (stepUpAuthorizationId is not { } authorizationId)
        {
            await _audit.RecordRefusalAsync(
                SecurityEvents.MoneyWithdrawalRefused, AuditOutcome.Refused,
                actorUserId: userId, subjectType: "Account", subjectId: account.Id,
                detail: ErrorCodes.AuthorizationRequired);
            throw new AuthenticationException(
                "This withdrawal has not been authorised.", ErrorCodes.AuthorizationRequired);
        }

        // Before any write, so an expired or mismatched authorisation costs nothing. The binding is
        // the same factory the mint used: a TRANSFER authorisation minted from this very account for
        // this very amount hashes differently -- the operation name is in the payload -- and is
        // refused as INVALID rather than quietly accepted.
        await _stepUp.ValidateAsync(
            userId, authorizationId, StepUpOperation.Withdrawal,
            StepUpBinding.ForWithdrawal(account.Id, request.Amount));

        /*
          AN EXPLICIT TRANSACTION, WHERE THIS METHOD USED TO HAVE NONE (ADR-0050 reserved the
          restructure for this change, and this is it).

          The ledger row and its MoneyWithdrawn audit row already committed as one unit -- the row
          rides the same SaveChanges (ADR-0044 D1). But ConsumeAsync is a separate ExecuteUpdate,
          and with no caller transaction it would AUTOCOMMIT beside that save: on SQL Server the
          DbContext opens its own transaction for the chain row, and a consume placed next to it
          lands outside. A withdrawal could then commit with its authorisation still Pending --
          spendable a second time -- or the authorisation could burn on a withdrawal that never
          committed. So the transaction is opened here, through the execution strategy
          (EnableRetryOnFailure refuses a user-initiated transaction outside one), and with it
          present the DbContext funnel applies the audit chain INSIDE it rather than opening its own.

          CONSUME AFTER THE SAVE, NOT BEFORE. ConsumeAsync throws when its UPDATE matches zero rows
          -- an authorisation spent by a concurrent request, or one that expired between Validate
          and here -- and that throw is what rolls the withdrawal back. Consuming first would spend
          the authorisation and then let the save fail on its own, which is the disagreement the
          transaction exists to prevent.
        */
        var strategy = _context.Database.CreateExecutionStrategy();
        WithdrawResponse? response = null;

        /*
          TWO RETRY LOOPS, ONE INSIDE THE OTHER, as in TransferService and AccountService. The
          execution strategy re-runs the delegate on a TRANSIENT fault; the loop around it re-runs
          the attempt when the account's RowVersion refuses a stale write -- a deposit that landed
          between the guard and the save, or a second withdrawal presenting this same authorisation
          that committed first. Bounded by ConcurrencyRetry.MaxAttempts, jittered between attempts,
          and the last failure propagates.
        */
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await strategy.ExecuteAsync(async () =>
                {
                    /*
                      RE-ENTRANCY DISCIPLINE, at the top of the delegate, because EnableRetryOnFailure
                      re-runs the WHOLE delegate against this SAME DbContext and the outer loop
                      re-enters it too. Each attempt rebuilds its state from the store, or it would
                      find the balance already decremented in memory and a second Added AuditEvent
                      still tracked from the attempt that failed -- one withdrawal, two rows.

                      AND IT IS THE CASE-B GUARD. A transient fault AFTER the commit is
                      indistinguishable from one before it, so the idempotency claim row is what
                      tells them apart: if a prior attempt already committed this withdrawal,
                      PrepareIdempotentAttemptAsync refuses to execute it again and the middleware
                      surfaces 409 RESULT_UNKNOWN. Without it, a lost acknowledgement would move the
                      money twice under one Idempotency-Key.
                    */
                    await ConcurrencyRetry.PrepareIdempotentAttemptAsync(_context, account);

                    /*
                      AND THE GUARD AGAIN, against the balance this attempt actually reloaded. The
                      check above ran on the pre-retry read; a withdrawal that raced this one may
                      have taken the money in between, and committing on the strength of the old
                      balance is exactly the overdraft the RowVersion exists to prevent.
                    */
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

                    account.Balance = balanceAfter;
                    account.UpdatedAt = DateTime.UtcNow;

                    _context.Transactions.Add(transaction);

                    await using var dbTransaction = await _context.Database.BeginTransactionAsync();

                    try
                    {
                        /*
                          Enlisted BEFORE the save, so the audit row and the movement are one unit:
                          if the audit insert fails, the money does not move either (ADR-0044 D1).

                          DETAIL NAMES THE AUTHORISATION THAT PAID FOR IT, and unlike a closure this
                          row ALSO has a ledger id for ConsumedByTransactionId to point at. Both
                          links are written on purpose: the pointer lives in the unchained
                          authorisation table, where a database writer can rewrite it, while this
                          name is under the row's hash. The evidence verb checks one against the
                          other, so agreement is what makes a withdrawal strongly authenticated and
                          disagreement is a finding rather than a shrug.
                        */
                        _audit.Record(
                            SecurityEvents.MoneyWithdrawn, AuditOutcome.Succeeded,
                            actorUserId: userId, subjectType: "Transaction", subjectId: transaction.Id,
                            detail: AuditDetails.ConsumedAuthorisation(authorizationId));

                        await _context.SaveChangesAsync();

                        // Spent inside this transaction and after the rows exist. consumedByTransactionId
                        // is the ledger row, NOT null as a closure passes: a withdrawal produces a
                        // movement, and the evidence verb joins the authorisation to it on this id.
                        await _stepUp.ConsumeAsync(
                            userId, authorizationId, consumedByTransactionId: transaction.Id);

                        await dbTransaction.CommitAsync();
                    }
                    catch
                    {
                        // Preserve the ORIGINAL fault (e.g. the transient the execution strategy must
                        // see to retry): rolling back a transaction whose connection or commit already
                        // failed can itself throw and would otherwise mask it.
                        try { await dbTransaction.RollbackAsync(); }
                        catch { /* best effort: the transaction may already be gone */ }
                        throw;
                    }

                    // No amount and no balance on the log line, for the reason on the deposit's.
                    _logger.LogInformation(
                        "Withdrawal from account {AccountId}. Transaction: {TransactionNumber}",
                        account.Id, transaction.TransactionNumber);

                    response = _mapper.ToWithdrawResponse(transaction, balanceAfter);
                });

                break;
            }
            catch (DbUpdateConcurrencyException ex) when (ConcurrencyRetry.ShouldRetry(ex, attempt))
            {
                _logger.LogInformation(
                    "Concurrency conflict on withdrawal from account {AccountId} (attempt {Attempt}); retrying",
                    account.Id, attempt);
                await ConcurrencyRetry.PrepareNextAttemptAsync(_context, account);
            }
            catch (DbUpdateException ex) when (ConcurrencyRetry.IsTransactionNumberCollision(ex, attempt))
            {
                // A regenerable clash on the transaction number: the next attempt mints a fresh one.
                // Why it is safe to retry, and why it is narrowed by INDEX NAME rather than by error
                // number, lives on ConcurrencyRetry.IsTransactionNumberCollision. Warning, not
                // Information: it should never happen, so an occurrence means the entropy assumption
                // deserves re-checking, which needs to know WHICH account.
                _logger.LogWarning(
                    ex,
                    "SecurityEvent {SecurityEvent}: transaction-number collision on withdrawal "
                        + "from account {AccountId} (attempt {Attempt}); regenerating",
                    SecurityEvents.TransactionNumberCollision, account.Id, attempt);
                await ConcurrencyRetry.PrepareNextAttemptAsync(_context, account);
            }
        }

        // Non-null on every path that reaches here: the loop only breaks after the delegate ran to
        // completion, and every other exit throws.
        return response!;
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
        /*
          THE OFFSET IS A LONG, and a page past the end is answered without a query. It was
          (Page - 1) * PageSize in int arithmetic while [Range] lets Page reach int.MaxValue, so it
          wrapped. Measured 2026-09-11 on SQL Server: Page=2147483647&PageSize=100 wrapped to
          OFFSET -200 and answered 500 ("The offset specified in a OFFSET clause may not be
          negative."), and Page=1073741825&PageSize=20 wrapped to exactly 0 and answered 200 with
          the FIRST page's rows, labelled page 1073741825. An offset too large for an int is past
          TotalItems, which is an int, so this comparison settles every such case before the cast.
        */
        var offset = (long)(filter.Page - 1) * filter.PageSize;
        List<Transaction> transactions = offset >= totalItems
            ? []
            : await query
                .OrderByDescending(t => t.CreatedAt)
                .ThenByDescending(t => t.Id)
                .Skip((int)offset)
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
