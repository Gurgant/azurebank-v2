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
using AzureBank.Shared.Constants;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// Transfer service handling external and internal money transfers.
/// </summary>
public class TransferService : ITransferService
{
    private readonly AzureBankDbContext _context;
    private readonly IAccountAccessService _accountAccess;
    private readonly UserMapper _userMapper;
    private readonly IPinVerifier _pinVerifier;
    private readonly IStepUpAuthorizationService _stepUp;
    private readonly ILogger<TransferService> _logger;
    private readonly IAuditService _audit;

    public TransferService(
        AzureBankDbContext context,
        IAccountAccessService accountAccess,
        UserMapper userMapper,
        IPinVerifier pinVerifier,
        IStepUpAuthorizationService stepUp,
        ILogger<TransferService> logger,
        IAuditService audit)
    {
        _audit = audit;
        _context = context;
        _accountAccess = accountAccess;
        _userMapper = userMapper;
        _pinVerifier = pinVerifier;
        _stepUp = stepUp;
        _logger = logger;
    }

    /// <summary>
    /// Refuses a transfer that presents no authorisation at all, at the rung the in-band PIN check
    /// used to occupy (ADR-0042).
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE RUNG IS THE POINT, not the check. It runs after the source account's ownership 404 and
    /// BEFORE the payee is resolved, which is exactly where <c>VerifyPinOrThrowAsync</c> ran. Keep
    /// it there: below the payee resolution, a caller holding no second factor could ask this
    /// endpoint which handles exist and which of them can receive money, one 404 or 422 at a time.
    /// The PIN check was providing that property silently; deleting it without putting something in
    /// its place would have removed it silently too.
    /// </para>
    /// <para>
    /// Split from <see cref="IStepUpAuthorizationService.ValidateAsync"/> deliberately, because the
    /// two questions become answerable at different moments: "you presented nothing" needs nothing
    /// but the header, while "what you presented does not match" needs the payee that only exists
    /// after the tag is resolved.
    /// </para>
    /// <para>
    /// MEASURED, and the reason the parameter stays <c>Guid?</c> rather than becoming required at
    /// the binding layer: an EMPTY <c>Step-Up-Authorization</c> header binds to <c>null</c>, exactly
    /// like an absent one — both reach here and both get this 401. A header that is present but not
    /// a UUID never reaches here at all; MVC model binding refuses it upstream with
    /// <c>400 {"Step-Up-Authorization": ["The value '…' is not valid."]}</c>. Making the parameter
    /// non-nullable would have replaced this 401 with a third shape — a model-state 400 carrying no
    /// <c>errorCode</c> — which is not what ADR-0042's error table promises.
    /// </para>
    /// </remarks>
    private static Guid RequireAuthorization(Guid? stepUpAuthorizationId)
    {
        if (stepUpAuthorizationId is not { } authorizationId)
        {
            throw new AuthenticationException(
                "This transfer has not been authorised.", ErrorCodes.AuthorizationRequired);
        }

        return authorizationId;
    }

    /*
      WHY THE AUTHORISATION IS CHECKED HERE AND NOT IN FRONT OF THE PIPELINE (ADR-0042).

      The obvious place for a step-up gate is the BFF's AuthLevelMiddleware, or a middleware beside
      the idempotency claim. Both are wrong, and the reason is measured rather than argued:
      IdempotencyMiddleware.cs:107-112 writes the stored response and RETURNS, never reaching
      `await _next(context)` at :126. A replay therefore runs no model binding, no validation, no
      controller and no service — so it also runs no authorisation check.

      That is the correct behaviour, and every payment API that documents the ordering agrees:
      authenticate the CALLER upstream of the replay lookup, authorise the CUSTOMER downstream of it.
      A gate in front of the claim would refuse a retry whose two-minute authorisation had lapsed
      BEFORE TryAcquireAsync could hand back the stored 201 — leaving the one client that provably
      cannot know whether its money moved unable to find out. The in-body PIN already has this
      property; the authorisation must keep it.

      THE HEADER IS NOW THE ONLY PROOF. It shipped optional for one PR — nothing sent it yet, and
      its absence fell through to the in-band PIN of ADR-0041 — but while both proofs are accepted
      the weaker one decides, and six static digits authorising any amount to any payee is the
      finding ADR-0042 opens with. `TransferRequest.Pin` is gone and a transfer without an
      authorisation is refused `401 AUTHORIZATION_REQUIRED`.

      Validation happens where the payee is known (an external transfer's binding names the
      RECIPIENT USER, which only exists after the tag is resolved) and before anything is written, so
      an expired authorisation still costs nothing. Consumption is deferred into the transaction
      below, so an authorisation is never spent by a transfer that rolled back.
    */

    /// <summary>
    /// Resolves an external transfer's payee, with the same three refusals in the same order the
    /// transfer has always applied: 404 for a sender whose row is gone, 422
    /// <c>SELF_TRANSFER_NOT_ALLOWED</c>, 404 <c>Recipient</c>, 422 <c>RECIPIENT_NO_ACCOUNT</c>.
    ///
    /// <para>
    /// Extracted so minting and sending cannot drift. An authorisation that could name a payee the
    /// transfer would then refuse is an authorisation for something that cannot happen, and the
    /// existing TransferEndpointTests are what prove the extraction faithful — they exercise all
    /// four refusals through the endpoint and never touched this method.
    /// </para>
    /// </summary>
    private async Task<(ApplicationUser Sender, ApplicationUser Recipient, Account RecipientAccount)>
        ResolveExternalPayeeAsync(Guid userId, string recipientAzureTag)
    {
        // Get sender user for self-transfer check
        var senderUser = await _context.Users.FindAsync(userId);
        if (senderUser == null)
        {
            throw new NotFoundException("User", userId);
        }

        // Prevent self-transfer
        if (senderUser.AzureTag.Equals(recipientAzureTag, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessRuleException(
                "Cannot transfer to yourself. Use internal account transfer instead.",
                ErrorCodes.SelfTransferNotAllowed);
        }

        // Find recipient by AzureTag
        var recipient = await _context.Users
            .Include(u => u.Accounts)
            .FirstOrDefaultAsync(u => u.AzureTag == recipientAzureTag.ToLower());

        if (recipient == null)
        {
            throw new NotFoundException("Recipient", recipientAzureTag);
        }

        // Get recipient's primary account
        var recipientAccount = recipient.Accounts.FirstOrDefault(a => a.IsPrimary && !a.IsDeleted)
            ?? recipient.Accounts.FirstOrDefault(a => !a.IsDeleted);

        if (recipientAccount == null)
        {
            throw new BusinessRuleException("Recipient does not have an active account.", ErrorCodes.RecipientNoAccount);
        }

        return (senderUser, recipient, recipientAccount);
    }

    /// <inheritdoc />
    public async Task<StepUpAuthorizationResponse> AuthoriseTransferAsync(
        Guid userId, TransferAuthorizationRequest request)
    {
        // Ownership first, exactly as the transfer does: an unknown source account is a 404 before
        // the PIN is ever consulted, so a probe of someone else's account costs no attempt.
        await _accountAccess.GetAccountWithOwnershipCheckAsync(request.FromAccountId, userId);

        var (_, recipient, _) = await ResolveExternalPayeeAsync(userId, request.RecipientAzureTag);

        var authorization = await _stepUp.MintAsync(
            userId,
            StepUpOperation.Transfer,
            new StepUpBinding(request.FromAccountId, null, recipient.Id, request.Amount),
            request.Pin);

        return new StepUpAuthorizationResponse
        {
            AuthorizationId = authorization.Id,
            ExpiresAt = authorization.ExpiresAt
        };
    }

    /// <inheritdoc />
    public async Task<StepUpAuthorizationResponse> AuthoriseInternalTransferAsync(
        Guid userId, InternalTransferAuthorizationRequest request)
    {
        await _accountAccess.GetAccountWithOwnershipCheckAsync(request.FromAccountId, userId);
        await _accountAccess.GetAccountWithOwnershipCheckAsync(request.ToAccountId, userId);

        if (request.FromAccountId == request.ToAccountId)
        {
            throw new BusinessRuleException("Cannot transfer to the same account.", ErrorCodes.SameAccountTransfer);
        }

        var authorization = await _stepUp.MintAsync(
            userId,
            StepUpOperation.InternalTransfer,
            new StepUpBinding(request.FromAccountId, request.ToAccountId, null, request.Amount),
            request.Pin);

        return new StepUpAuthorizationResponse
        {
            AuthorizationId = authorization.Id,
            ExpiresAt = authorization.ExpiresAt
        };
    }

    /// <inheritdoc />
    public async Task<TransferResponse> TransferAsync(
        Guid userId, TransferRequest request, Guid? stepUpAuthorizationId)
    {
        // Get sender's account with ownership check
        var fromAccount = await _accountAccess.GetAccountWithOwnershipCheckAsync(request.FromAccountId, userId);

        /*
          RECORDED AT THE CALL SITE, not inside RequireAuthorization, for a reason that decides the
          row's usefulness: the helper is static and runs before nothing, while HERE the sender's
          account is already resolved -- so the refusal can name the account it was refused against
          instead of naming nobody. Both transfer kinds go through the same helper, so both record.
        */
        Guid authorizationId;
        try
        {
            authorizationId = RequireAuthorization(stepUpAuthorizationId);
        }
        catch (AuthenticationException)
        {
            await _audit.RecordRefusalAsync(
                SecurityEvents.MoneyTransferRefused, AuditOutcome.Refused,
                actorUserId: userId, subjectType: "Account", subjectId: fromAccount.Id,
                detail: ErrorCodes.AuthorizationRequired);
            throw;
        }

        var (senderUser, recipient, recipientAccount) =
            await ResolveExternalPayeeAsync(userId, request.RecipientAzureTag);

        // Bound to recipient.Id, not the tag: a handle is renameable (ADR-0015), so an authorisation
        // naming @admin would survive @admin becoming someone else's handle.
        var binding = new StepUpBinding(request.FromAccountId, null, recipient.Id, request.Amount);
        await _stepUp.ValidateAsync(userId, authorizationId, StepUpOperation.Transfer, binding);

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

                        /*
                          No `now` here on purpose. AzureBankDbContext.UpdateTimestamps owns
                          CreatedAt/UpdatedAt and runs inside SaveChanges, so anything this method
                          assigned was overwritten a moment later. The one value that used to
                          SURVIVE was the copy handed back as ProcessedAt — so the API reported an
                          instant the database never held. ProcessedAt is now read back from the
                          persisted row, below.
                        */

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
                            Status = TransactionStatus.Completed
                        };

                        // Create incoming transaction (recipient)
                        var incomingTransaction = new Transaction
                        {
                            Id = Guid.CreateVersion7(),
                            // Own number in the documented format: the old "-R"
                            // suffix appended to the OUTGOING number overflowed
                            // the column, and SQL Server rejected EVERY transfer
                            // with a truncation error. Generating a second number
                            // is also what keeps the check symbol correct on both
                            // rows. The pair is linked by RelatedTransactionId.
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
                            Status = TransactionStatus.Completed
                        };

                        // Update balances
                        fromAccount.Balance -= request.Amount;
                        recipientAccount.Balance += request.Amount;

                        // Save (one-directional link only)
                        _context.Transactions.Add(outgoingTransaction);
                        _context.Transactions.Add(incomingTransaction);

                        /*
                          ONE ROW FOR ONE ACT. A transfer writes two ledger rows — a debit and a
                          credit — but they are the bookkeeping of a single thing the actor did, so
                          the audit trail records it once. The subject is the OUTGOING row: the one
                          whose owner IS the actor, and the one the step-up authorisation is consumed
                          against a few lines below.

                          That choice matters more here than anywhere else in this file. The incoming
                          row lands on the PAYEE's account, and its Account.UserId is provably not the
                          acting principal — the payee is resolved by handle with no ownership check,
                          and the self-transfer guard plus the unique AzureTag index make the two ids
                          different by construction. Subjecting the audit row to that row would name
                          the wrong person.

                          Detail stays null, as on every money event: amount, counterparty and
                          description are on the ledger rows SubjectId reaches (ADR-0044 D5).
                        */
                        _audit.Record(
                            SecurityEvents.MoneyTransferred, AuditOutcome.Succeeded,
                            actorUserId: userId, subjectType: "Transaction",
                            subjectId: outgoingTransaction.Id);
                        await _context.SaveChangesAsync();

                        // Write-once back-link (permitted by the immutability
                        // guard: RelatedTransactionId null -> value only)
                        outgoingTransaction.RelatedTransactionId = incomingTransaction.Id;
                        await _context.SaveChangesAsync();

                        /*
                          Spend the authorisation INSIDE this transaction, and after the rows exist
                          so the evidence can name the movement it paid for. One set-based UPDATE
                          conditional on the row still being Pending: two concurrent transfers
                          presenting the same authorisation resolve to exactly one success, because
                          the loser matches zero rows and ConsumeAsync throws — which rolls this
                          transaction back rather than moving money on a spent authorisation.
                        */
                        if (stepUpAuthorizationId is { } toConsume)
                        {
                            await _stepUp.ConsumeAsync(userId, toConsume, outgoingTransaction.Id);
                        }

                        await dbTransaction.CommitAsync();

                        // No amount in the log line: logs are exported (Loki), and a money amount
                        // is financial data — the transaction number is the audit-trail key.
                        _logger.LogInformation(
                            "Transfer from {SenderTag} to {RecipientTag} completed. Transaction: {TransactionNumber}",
                            senderUser.AzureTag, recipient.AzureTag, transactionNumber);
                        ApiMetrics.Transfers.Add(1, new KeyValuePair<string, object?>("azurebank.kind", "external"));

                        return new TransferResponse
                        {
                            TransactionNumber = transactionNumber,
                            Amount = request.Amount,
                            NewBalance = fromAccount.Balance,
                            RecipientAzureTag = recipient.AzureTag,
                            RecipientName = $"{recipient.FirstName} {recipient.LastName[0]}.",
                            // The PERSISTED instant: UpdateTimestamps stamped it during the first
                            // SaveChanges above, onto this tracked entity.
                            ProcessedAt = outgoingTransaction.CreatedAt
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
                    "SecurityEvent {SecurityEvent}: transaction-number collision on transfer from "
                        + "account {AccountId} to {RecipientAccountId} (attempt {Attempt}); regenerating",
                    SecurityEvents.TransactionNumberCollision, fromAccount.Id, recipientAccount.Id, attempt);
                await ConcurrencyRetry.PrepareNextAttemptAsync(_context, fromAccount, recipientAccount);
            }
        }
    }

    /// <inheritdoc />
    public async Task<InternalTransferResponse> InternalTransferAsync(
        Guid userId, InternalTransferRequest request, Guid? stepUpAuthorizationId)
    {
        // Validate accounts belong to user
        var fromAccount = await _accountAccess.GetAccountWithOwnershipCheckAsync(request.FromAccountId, userId);
        var toAccount = await _accountAccess.GetAccountWithOwnershipCheckAsync(request.ToAccountId, userId);

        /*
          AFTER BOTH OWNERSHIP CHECKS here, where the external path refuses before resolving its
          payee — and the asymmetry is deliberate rather than an oversight.

          It is the rung `VerifyPinOrThrowAsync` occupied on this method, and the reason the two
          differ is what the check upstream of it reveals. On the external path that is SOMEONE
          ELSE's handle, so answering it to a caller holding no second factor would turn the
          endpoint into a directory. Here both accounts are the caller's OWN: `GetAccountWithOwner-
          shipCheckAsync` tells them only what `GET /api/accounts` already does, so there is nothing
          to withhold and the more specific refusal is the more useful one.
        */
        /*
          RECORDED AT THE CALL SITE, not inside RequireAuthorization, for a reason that decides the
          row's usefulness: the helper is static and runs before nothing, while HERE the sender's
          account is already resolved -- so the refusal can name the account it was refused against
          instead of naming nobody. Both transfer kinds go through the same helper, so both record.
        */
        Guid authorizationId;
        try
        {
            authorizationId = RequireAuthorization(stepUpAuthorizationId);
        }
        catch (AuthenticationException)
        {
            await _audit.RecordRefusalAsync(
                SecurityEvents.MoneyTransferRefused, AuditOutcome.Refused,
                // fromAccount, NOT toAccount: the subject is the account the money would have LEFT
                // and the one the step-up is consumed against, which is the same rule the four
                // success events follow. An internal transfer resolves both accounts before this
                // point, so the nearer variable is the destination -- and naming it here would put
                // the refusal on the wrong account while every test still passed.
                actorUserId: userId, subjectType: "Account", subjectId: fromAccount.Id,
                detail: ErrorCodes.AuthorizationRequired);
            throw;
        }

        // Same account check (should be caught by validator, but double-check)
        if (request.FromAccountId == request.ToAccountId)
        {
            throw new BusinessRuleException("Cannot transfer to the same account.", ErrorCodes.SameAccountTransfer);
        }

        // The payee is the payer, so the binding names the destination ACCOUNT instead of a
        // recipient user. Same hash definition, different populated fields.
        await _stepUp.ValidateAsync(
            userId,
            authorizationId,
            StepUpOperation.InternalTransfer,
            new StepUpBinding(request.FromAccountId, request.ToAccountId, null, request.Amount));

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

                        // No `now` here — same reason as the external transfer above:
                        // UpdateTimestamps owns the stamp, and ProcessedAt is read back below.

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
                            Status = TransactionStatus.Completed
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
                            Status = TransactionStatus.Completed
                        };

                        // Update balances
                        fromAccount.Balance -= request.Amount;
                        toAccount.Balance += request.Amount;

                        // Save (one-directional link only)
                        _context.Transactions.Add(outgoingTransaction);
                        _context.Transactions.Add(incomingTransaction);

                        // Same shape as the external transfer above, and a DIFFERENT event on
                        // purpose: a move between the actor's own accounts and a payment to a third
                        // party do not carry the same weight, and an evidence pack should not have
                        // to re-derive which one this was from the ledger.
                        _audit.Record(
                            SecurityEvents.MoneyTransferredInternally, AuditOutcome.Succeeded,
                            actorUserId: userId, subjectType: "Transaction",
                            subjectId: outgoingTransaction.Id);
                        await _context.SaveChangesAsync();

                        // Write-once back-link (see immutability guard)
                        outgoingTransaction.RelatedTransactionId = incomingTransaction.Id;
                        await _context.SaveChangesAsync();

                        // Spent inside the transaction, same as the external transfer above.
                        if (stepUpAuthorizationId is { } toConsume)
                        {
                            await _stepUp.ConsumeAsync(userId, toConsume, outgoingTransaction.Id);
                        }

                        await dbTransaction.CommitAsync();

                        // No amount in the log line (financial data in an exported log — see above).
                        _logger.LogInformation(
                            "Internal transfer from account {FromId} to {ToId}. Transaction: {TransactionNumber}",
                            fromAccount.Id, toAccount.Id, transactionNumber);
                        ApiMetrics.Transfers.Add(1, new KeyValuePair<string, object?>("azurebank.kind", "internal"));

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
                            // The PERSISTED instant, not a copy the hook then overwrote.
                            ProcessedAt = outgoingTransaction.CreatedAt
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
                    "SecurityEvent {SecurityEvent}: transaction-number collision on internal transfer "
                        + "from account {AccountId} to {ToAccountId} (attempt {Attempt}); regenerating",
                    SecurityEvents.TransactionNumberCollision, fromAccount.Id, toAccount.Id, attempt);
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
