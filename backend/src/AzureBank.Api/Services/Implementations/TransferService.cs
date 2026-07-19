using AzureBank.Api.Mappers;
using AzureBank.Api.Observability;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Utilities;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// Transfer service handling external and internal money transfers.
/// </summary>
public class TransferService : ITransferService
{
    private readonly AzureBankDbContext _context;
    private readonly IAccountAccessService _accountAccess;
    private readonly UserMapper _userMapper;
    private readonly ILogger<TransferService> _logger;

    public TransferService(
        AzureBankDbContext context,
        IAccountAccessService accountAccess,
        UserMapper userMapper,
        ILogger<TransferService> logger)
    {
        _context = context;
        _accountAccess = accountAccess;
        _userMapper = userMapper;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TransferResponse> TransferAsync(Guid userId, TransferRequest request)
    {
        // Get sender's account with ownership check
        var fromAccount = await _accountAccess.GetAccountWithOwnershipCheckAsync(request.FromAccountId, userId);

        // Get sender user for self-transfer check
        var senderUser = await _context.Users.FindAsync(userId);
        if (senderUser == null)
        {
            throw new NotFoundException("User", userId);
        }

        // Prevent self-transfer
        if (senderUser.AzureTag.Equals(request.RecipientAzureTag, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessRuleException(
                "Cannot transfer to yourself. Use internal account transfer instead.",
                "SELF_TRANSFER_NOT_ALLOWED");
        }

        // Find recipient by AzureTag
        var recipient = await _context.Users
            .Include(u => u.Accounts)
            .FirstOrDefaultAsync(u => u.AzureTag == request.RecipientAzureTag.ToLower());

        if (recipient == null)
        {
            throw new NotFoundException("Recipient", request.RecipientAzureTag);
        }

        // Get recipient's primary account
        var recipientAccount = recipient.Accounts.FirstOrDefault(a => a.IsPrimary && !a.IsDeleted)
            ?? recipient.Accounts.FirstOrDefault(a => !a.IsDeleted);

        if (recipientAccount == null)
        {
            throw new BusinessRuleException("Recipient does not have an active account.", "RECIPIENT_NO_ACCOUNT");
        }

        // Use transaction for atomicity; retry optimistic-concurrency
        // conflicts on the accounts (see ConcurrencyRetry).
        var strategy = _context.Database.CreateExecutionStrategy();

        for (var attempt = 1; ; attempt++)
        {
            // Check sufficient funds (inside the loop: a reloaded balance
            // may no longer cover the transfer)
            if (fromAccount.Balance < request.Amount)
            {
                throw new InsufficientFundsException(fromAccount.Balance, request.Amount);
            }

            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    // EnableRetryOnFailure re-runs this whole delegate on a
                    // transient fault against the SAME DbContext. Make each
                    // attempt idempotent: discard the failed attempt's tracked
                    // work (Case A) and never re-execute an already-committed
                    // transfer (Case B). See PrepareTransferAttemptAsync.
                    await PrepareTransferAttemptAsync(fromAccount, recipientAccount);

                    // Funds re-check against the reloaded balance: a transient
                    // retry may run after a concurrent debit moved the balance.
                    if (fromAccount.Balance < request.Amount)
                    {
                        throw new InsufficientFundsException(fromAccount.Balance, request.Amount);
                    }

                    await using var dbTransaction = await _context.Database.BeginTransactionAsync();

                    try
                    {
                        var transactionNumber = IdGenerator.GenerateTransactionNumber();
                        var now = DateTime.UtcNow;

                        // Create outgoing transaction (sender).
                        // RelatedTransactionId stays null for now: the pair
                        // references each other, and two mutually referencing
                        // INSERTs are a circular FK dependency SQL Server
                        // cannot order. The back-link is written once below,
                        // inside this same transaction.
                        var outgoingTransaction = new Transaction
                        {
                            Id = Guid.CreateVersion7(),
                            TransactionNumber = transactionNumber,
                            AccountId = fromAccount.Id,
                            Account = fromAccount,
                            Type = TransactionType.TransferOut,
                            Amount = request.Amount,
                            BalanceBefore = fromAccount.Balance,
                            BalanceAfter = fromAccount.Balance - request.Amount,
                            Description = request.Description ?? $"Transfer to @{recipient.AzureTag}",
                            RecipientAzureTag = recipient.AzureTag,
                            Status = TransactionStatus.Completed,
                            CreatedAt = now
                        };

                        // Create incoming transaction (recipient)
                        var incomingTransaction = new Transaction
                        {
                            Id = Guid.CreateVersion7(),
                            // Own number in the documented TXN-YYYYMMDD-XXXXXX
                            // format: the old "-R" suffix exceeded the column's
                            // max length (20) — SQL Server rejected EVERY
                            // transfer with a truncation error. The pair is
                            // linked by RelatedTransactionId.
                            TransactionNumber = IdGenerator.GenerateTransactionNumber(),
                            AccountId = recipientAccount.Id,
                            Account = recipientAccount,
                            Type = TransactionType.TransferIn,
                            Amount = request.Amount,
                            BalanceBefore = recipientAccount.Balance,
                            BalanceAfter = recipientAccount.Balance + request.Amount,
                            Description = request.Description ?? $"Transfer from @{senderUser.AzureTag}",
                            SenderAzureTag = senderUser.AzureTag,
                            RelatedTransactionId = outgoingTransaction.Id,
                            Status = TransactionStatus.Completed,
                            CreatedAt = now
                        };

                        // Update balances
                        fromAccount.Balance -= request.Amount;
                        fromAccount.UpdatedAt = now;
                        recipientAccount.Balance += request.Amount;
                        recipientAccount.UpdatedAt = now;

                        // Save (one-directional link only)
                        _context.Transactions.Add(outgoingTransaction);
                        _context.Transactions.Add(incomingTransaction);
                        await _context.SaveChangesAsync();

                        // Write-once back-link (permitted by the immutability
                        // guard: RelatedTransactionId null -> value only)
                        outgoingTransaction.RelatedTransactionId = incomingTransaction.Id;
                        await _context.SaveChangesAsync();

                        await dbTransaction.CommitAsync();

                        _logger.LogInformation(
                            "Transfer of {Amount:C} from {SenderTag} to {RecipientTag} completed. Transaction: {TransactionNumber}",
                            request.Amount, senderUser.AzureTag, recipient.AzureTag, transactionNumber);
                        ApiMetrics.Transfers.Add(1, new KeyValuePair<string, object?>("kind", "external"));

                        return new TransferResponse
                        {
                            TransactionNumber = transactionNumber,
                            Amount = request.Amount,
                            NewBalance = fromAccount.Balance,
                            RecipientAzureTag = recipient.AzureTag,
                            RecipientName = $"{recipient.FirstName} {recipient.LastName[0]}.",
                            ProcessedAt = now
                        };
                    }
                    catch
                    {
                        // Preserve the ORIGINAL fault (e.g. the transient the
                        // execution strategy must see to retry): rolling back a
                        // transaction whose connection/commit already failed can
                        // itself throw and would otherwise mask it.
                        try { await dbTransaction.RollbackAsync(); }
                        catch { /* best effort: the transaction may already be gone */ }
                        throw;
                    }
                });
            }
            catch (DbUpdateConcurrencyException ex) when (ConcurrencyRetry.ShouldRetry(ex, attempt))
            {
                _logger.LogInformation(
                    "Concurrency conflict on transfer from account {AccountId} (attempt {Attempt}); retrying",
                    fromAccount.Id, attempt);
                await ConcurrencyRetry.PrepareNextAttemptAsync(_context, fromAccount, recipientAccount);
            }
        }
    }

    /// <inheritdoc />
    public async Task<InternalTransferResponse> InternalTransferAsync(Guid userId, InternalTransferRequest request)
    {
        // Validate accounts belong to user
        var fromAccount = await _accountAccess.GetAccountWithOwnershipCheckAsync(request.FromAccountId, userId);
        var toAccount = await _accountAccess.GetAccountWithOwnershipCheckAsync(request.ToAccountId, userId);

        // Same account check (should be caught by validator, but double-check)
        if (request.FromAccountId == request.ToAccountId)
        {
            throw new BusinessRuleException("Cannot transfer to the same account.", "SAME_ACCOUNT_TRANSFER");
        }

        // Use transaction for atomicity; retry optimistic-concurrency
        // conflicts on the accounts (see ConcurrencyRetry).
        var strategy = _context.Database.CreateExecutionStrategy();

        for (var attempt = 1; ; attempt++)
        {
            // Check sufficient funds (inside the loop: a reloaded balance
            // may no longer cover the transfer)
            if (fromAccount.Balance < request.Amount)
            {
                throw new InsufficientFundsException(fromAccount.Balance, request.Amount);
            }

            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    // EnableRetryOnFailure re-runs this whole delegate on a
                    // transient fault against the SAME DbContext. Make each
                    // attempt idempotent: discard the failed attempt's tracked
                    // work (Case A) and never re-execute an already-committed
                    // transfer (Case B). See PrepareTransferAttemptAsync.
                    await PrepareTransferAttemptAsync(fromAccount, toAccount);

                    // Funds re-check against the reloaded balance: a transient
                    // retry may run after a concurrent debit moved the balance.
                    if (fromAccount.Balance < request.Amount)
                    {
                        throw new InsufficientFundsException(fromAccount.Balance, request.Amount);
                    }

                    await using var dbTransaction = await _context.Database.BeginTransactionAsync();

                    try
                    {
                        var transactionNumber = IdGenerator.GenerateTransactionNumber();
                        var now = DateTime.UtcNow;

                        // Create outgoing transaction. RelatedTransactionId
                        // stays null: mutual references cannot be inserted in
                        // one shot (circular FK); back-link written below.
                        var outgoingTransaction = new Transaction
                        {
                            Id = Guid.CreateVersion7(),
                            TransactionNumber = transactionNumber,
                            AccountId = fromAccount.Id,
                            Account = fromAccount,
                            Type = TransactionType.TransferOut,
                            Amount = request.Amount,
                            BalanceBefore = fromAccount.Balance,
                            BalanceAfter = fromAccount.Balance - request.Amount,
                            Description = request.Description ?? $"Internal transfer to {toAccount.Name}",
                            Status = TransactionStatus.Completed,
                            CreatedAt = now
                        };

                        // Create incoming transaction
                        var incomingTransaction = new Transaction
                        {
                            Id = Guid.CreateVersion7(),
                            // Own number (documented format; the old "-I"
                            // suffix exceeded the column max length, see above)
                            TransactionNumber = IdGenerator.GenerateTransactionNumber(),
                            AccountId = toAccount.Id,
                            Account = toAccount,
                            Type = TransactionType.TransferIn,
                            Amount = request.Amount,
                            BalanceBefore = toAccount.Balance,
                            BalanceAfter = toAccount.Balance + request.Amount,
                            Description = request.Description ?? $"Internal transfer from {fromAccount.Name}",
                            RelatedTransactionId = outgoingTransaction.Id,
                            Status = TransactionStatus.Completed,
                            CreatedAt = now
                        };

                        // Update balances
                        fromAccount.Balance -= request.Amount;
                        fromAccount.UpdatedAt = now;
                        toAccount.Balance += request.Amount;
                        toAccount.UpdatedAt = now;

                        // Save (one-directional link only)
                        _context.Transactions.Add(outgoingTransaction);
                        _context.Transactions.Add(incomingTransaction);
                        await _context.SaveChangesAsync();

                        // Write-once back-link (see immutability guard)
                        outgoingTransaction.RelatedTransactionId = incomingTransaction.Id;
                        await _context.SaveChangesAsync();

                        await dbTransaction.CommitAsync();

                        _logger.LogInformation(
                            "Internal transfer of {Amount:C} from account {FromId} to {ToId}. Transaction: {TransactionNumber}",
                            request.Amount, fromAccount.Id, toAccount.Id, transactionNumber);
                        ApiMetrics.Transfers.Add(1, new KeyValuePair<string, object?>("kind", "internal"));

                        return new InternalTransferResponse
                        {
                            TransferId = outgoingTransaction.Id,
                            TransactionNumber = transactionNumber,
                            FromAccountId = fromAccount.Id,
                            ToAccountId = toAccount.Id,
                            Amount = request.Amount,
                            Description = request.Description,
                            FromAccountNewBalance = fromAccount.Balance,
                            ToAccountNewBalance = toAccount.Balance,
                            ProcessedAt = now
                        };
                    }
                    catch
                    {
                        // Preserve the ORIGINAL fault (e.g. the transient the
                        // execution strategy must see to retry): rolling back a
                        // transaction whose connection/commit already failed can
                        // itself throw and would otherwise mask it.
                        try { await dbTransaction.RollbackAsync(); }
                        catch { /* best effort: the transaction may already be gone */ }
                        throw;
                    }
                });
            }
            catch (DbUpdateConcurrencyException ex) when (ConcurrencyRetry.ShouldRetry(ex, attempt))
            {
                _logger.LogInformation(
                    "Concurrency conflict on internal transfer from account {AccountId} (attempt {Attempt}); retrying",
                    fromAccount.Id, attempt);
                await ConcurrencyRetry.PrepareNextAttemptAsync(_context, fromAccount, toAccount);
            }
        }
    }

    /// <summary>
    /// Idempotent reset run at the TOP of every transfer execution attempt.
    /// The EF execution strategy (EnableRetryOnFailure, production only) re-runs
    /// the delegate on a transient fault against the SAME shared DbContext, so
    /// each attempt must rebuild the same state from scratch.
    ///
    /// Case A — the fault hit before/at the business SaveChanges (nothing
    /// committed): <see cref="ConcurrencyRetry.ResetToStoreAsync"/> detaches the
    /// leftover Added/Unchanged Transaction rows and reloads the accounts, so a
    /// fresh attempt does not double the transactions or the balance mutations.
    ///
    /// Case B — the fault hit after the transaction actually committed (commit
    /// ack lost): the idempotency record is already Executed/Completed in the
    /// DATABASE. Re-executing would post a SECOND real transfer, so we re-read
    /// the record fresh and signal the documented "committed, response unknown"
    /// path (409 IDEMPOTENCY_RESULT_UNKNOWN) instead of creating new rows.
    ///
    /// If the record is still Processing (nothing committed), we realign the
    /// tracked record to database truth and re-apply the pending Executed flip
    /// (rotating the fencing token) so it rides THIS attempt's SaveChanges even
    /// if a prior attempt's AcceptAllChanges had already marked it Unchanged.
    ///
    /// The idempotency step is a no-op when no record is tracked (e.g. a direct
    /// service-level call outside the middleware): there is nothing to guard.
    /// </summary>
    private async Task PrepareTransferAttemptAsync(params Account[] accounts)
    {
        await ConcurrencyRetry.ResetToStoreAsync(_context, accounts);

        var entry = _context.ChangeTracker.Entries<IdempotencyRecord>().FirstOrDefault();
        if (entry is null)
        {
            return;
        }

        // Fresh database truth for this claim. ReloadAsync also refreshes the
        // tracked ORIGINAL values, so the flip re-applied below emits a fenced
        // UPDATE (WHERE ClaimId = <db value>) that rides this attempt's commit.
        await entry.ReloadAsync();

        if (entry.State == EntityState.Detached
            || entry.Entity.Status is IdempotencyStatus.Executed or IdempotencyStatus.Completed)
        {
            // Detached: the row was deleted under us (stale takeover/cleanup) —
            // we cannot prove nothing committed. Executed/Completed: a prior
            // attempt already committed this transfer. Either way, refuse to
            // execute again; the middleware surfaces this as 409 RESULT_UNKNOWN.
            throw IdempotencyException.ResultUnknown();
        }

        // Processing: nothing committed yet. Re-arm the pending Executed flip so
        // it travels atomically with this attempt's business commit.
        entry.Entity.Status = IdempotencyStatus.Executed;
        entry.Entity.ClaimId = Guid.NewGuid();
    }
}
