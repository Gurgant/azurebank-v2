using AzureBank.Api.Mappers;
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
                        await dbTransaction.RollbackAsync();
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
                        await dbTransaction.RollbackAsync();
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
}
