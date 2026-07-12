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

        // Check sufficient funds
        if (fromAccount.Balance < request.Amount)
        {
            throw new InsufficientFundsException(fromAccount.Balance, request.Amount);
        }

        // Use transaction for atomicity
        var strategy = _context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();

            try
            {
                var transactionNumber = IdGenerator.GenerateTransactionNumber();
                var now = DateTime.UtcNow;

                // Create outgoing transaction (sender)
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
                    TransactionNumber = $"{transactionNumber}-R",
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

                // Link transactions
                outgoingTransaction.RelatedTransactionId = incomingTransaction.Id;

                // Update balances
                fromAccount.Balance -= request.Amount;
                fromAccount.UpdatedAt = now;
                recipientAccount.Balance += request.Amount;
                recipientAccount.UpdatedAt = now;

                // Save
                _context.Transactions.Add(outgoingTransaction);
                _context.Transactions.Add(incomingTransaction);
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

        // Check sufficient funds
        if (fromAccount.Balance < request.Amount)
        {
            throw new InsufficientFundsException(fromAccount.Balance, request.Amount);
        }

        // Use transaction for atomicity
        var strategy = _context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync();

            try
            {
                var transactionNumber = IdGenerator.GenerateTransactionNumber();
                var now = DateTime.UtcNow;

                // Create outgoing transaction
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
                    TransactionNumber = $"{transactionNumber}-I",
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

                // Link transactions
                outgoingTransaction.RelatedTransactionId = incomingTransaction.Id;

                // Update balances
                fromAccount.Balance -= request.Amount;
                fromAccount.UpdatedAt = now;
                toAccount.Balance += request.Amount;
                toAccount.UpdatedAt = now;

                // Save
                _context.Transactions.Add(outgoingTransaction);
                _context.Transactions.Add(incomingTransaction);
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
}
