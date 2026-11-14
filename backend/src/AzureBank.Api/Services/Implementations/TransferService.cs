using AzureBank.Api.Mappers;
using AzureBank.Api.Observability;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using AzureBank.Shared.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    private readonly IDailyOutflowLimit _dailyLimit;
    private readonly DailyLimitOptions _dailyLimitOptions;

    /*
      dailyLimitOptions is read for the lock's wait bound only: the ceiling is IDailyOutflowLimit's
      to know, and nothing here computes a limit refusal. It is taken as IOptions, not as a number,
      so a test host's UseSetting reaches it by the same path an appsettings value does.
    */
    public TransferService(
        AzureBankDbContext context,
        IAccountAccessService accountAccess,
        UserMapper userMapper,
        IPinVerifier pinVerifier,
        IStepUpAuthorizationService stepUp,
        ILogger<TransferService> logger,
        IAuditService audit,
        IDailyOutflowLimit dailyLimit,
        IOptions<DailyLimitOptions> dailyLimitOptions)
    {
        _audit = audit;
        _context = context;
        _accountAccess = accountAccess;
        _userMapper = userMapper;
        _pinVerifier = pinVerifier;
        _stepUp = stepUp;
        _logger = logger;
        _dailyLimit = dailyLimit;
        _dailyLimitOptions = dailyLimitOptions.Value;
    }

    /*
      The per-user lock under which the transfer's transaction checks the day's ceiling (ADR-0050
      D5). sp_getapplock reports a refused lock as a negative return value, which ExecuteSqlRawAsync
      does not see, so the batch throws on one: a refused lock fails the transfer and never lets the
      sum run unserialised. A timeout is a fault, never DAILY_LIMIT_EXCEEDED.
    */
    internal const string DailyLimitLockSql =
        "DECLARE @r int, @t int = {1}; "
        + "EXEC @r = sp_getapplock @Resource = {0}, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @t; "
        + "IF @r < 0 BEGIN DECLARE @m nvarchar(200) = CASE WHEN @r = -1 "
        + "THEN CONCAT(N'sp_getapplock timed out after ', @t, N' ms waiting for the payer''s daily-limit lock') "
        + "ELSE CONCAT(N'sp_getapplock returned ', @r) END; THROW 50000, @m, 1; END";

    /// <summary>
    /// The applock resource name for one user's day: one lock per payer, never per account.
    /// </summary>
    internal static string DailyLimitLockResource(Guid userId) => $"daily-limit:{userId:D}";

    /// <summary>
    /// Refuses a transfer that presents no authorisation: 401 <c>AUTHORIZATION_REQUIRED</c>. An
    /// external transfer reaches it at the rung before the payee is resolved, an internal one after
    /// both ownership checks (ADR-0042 D8). The parameter stays <c>Guid?</c>: an empty header binds
    /// to <c>null</c> and reaches this 401, where a required one would answer a 400 with no
    /// <c>errorCode</c>.
    /// </summary>
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
      The authorisation is checked in this service and not in a middleware in front of the pipeline:
      on a replay IdempotencyMiddleware returns the stored response without calling the next step,
      so a gate in front of it would refuse a retry whose authorisation had lapsed. The header is
      the only proof, and it is spent inside the transaction (ADR-0042 D2, D6 and D8).
    */

    /// <summary>
    /// Resolves an external transfer's payee for the mint and the transfer alike, so that an
    /// authorisation never names a payee the transfer would refuse. A payee is resolved only inside
    /// the sender's demo copy (ADR-0062 decision 7): outside the demo both copy ids are null, and
    /// null matches null.
    /// </summary>
    private async Task<(ApplicationUser Sender, ApplicationUser Recipient, Account RecipientAccount)>
        ResolveExternalPayeeAsync(Guid userId, string recipientAzureTag, CancellationToken cancellationToken)
    {
        var senderUser = await _context.Users.FindAsync([userId], cancellationToken);
        if (senderUser == null)
        {
            throw new NotFoundException("User", userId);
        }

        if (senderUser.AzureTag.Equals(recipientAzureTag, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessRuleException(
                "Cannot transfer to yourself. Use internal account transfer instead.",
                ErrorCodes.SelfTransferNotAllowed);
        }

        var recipient = await _context.Users
            .FirstOrDefaultAsync(
                u => u.AzureTag == recipientAzureTag.ToLower() && u.DemoCopyId == senderUser.DemoCopyId,
                cancellationToken);

        if (recipient == null)
        {
            throw new RecipientNotFoundException(recipientAzureTag);
        }

        // The account to pay: the first of the payee's open accounts, in the one order both looks use.
        var recipientAccount =
            (await OpenAccountsInPayingOrderAsync(recipient.Id, cancellationToken)).FirstOrDefault();

        if (recipientAccount == null)
        {
            throw RecipientHasNoActiveAccount();
        }

        return (senderUser, recipient, recipientAccount);
    }

    /// <summary>
    /// The refusal for a payee with no account the money can go to. It is given where the payee is
    /// resolved and where a reload finds the payee's account closed with no other open.
    /// </summary>
    private static BusinessRuleException RecipientHasNoActiveAccount() =>
        new("Recipient does not have an active account.", ErrorCodes.RecipientNoAccount);

    /// <summary>
    /// A payee's open accounts in the order they are paid: the primary first, then the oldest, then
    /// the lowest id. Both looks of a transfer read it, so they choose the same account. The order
    /// is the store's, since it and .NET do not sort a <c>uniqueidentifier</c> alike, and closed
    /// accounts leave the list through the query filter.
    /// </summary>
    private Task<List<Account>> OpenAccountsInPayingOrderAsync(Guid payeeId, CancellationToken cancellationToken) =>
        _context.Accounts
            .Where(a => a.UserId == payeeId)
            .OrderByDescending(a => a.IsPrimary)
            .ThenBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// A payee account that closed or went since the first look is replaced by another open account
    /// of the same payee, in the paying order; with none left the answer is the 422 of a payee with
    /// no account, and the handle is not resolved again (ADR-0049 D9). Never a
    /// <see cref="NotFoundException"/> for the payee's account: its message carries the account id,
    /// and a payer knows a payee by handle only.
    /// </summary>
    private async Task<Account> RefuseOrChooseThePayeeAccountAgainAsync(
        Account fromAccount, ApplicationUser recipient, Account recipientAccount,
        CancellationToken cancellationToken)
    {
        ConcurrencyRetry.RefuseIfClosed(_context, fromAccount);

        if (!ConcurrencyRetry.IsClosedOrGone(_context, recipientAccount))
        {
            return recipientAccount;
        }

        foreach (var candidate in await OpenAccountsInPayingOrderAsync(recipient.Id, cancellationToken))
        {
            await _context.Entry(candidate).ReloadAsync(cancellationToken);

            if (!ConcurrencyRetry.IsClosedOrGone(_context, candidate))
            {
                return candidate;
            }
        }

        throw RecipientHasNoActiveAccount();
    }

    /// <summary>
    /// What an external transfer looks at between the reload of its accounts and its save. Returns
    /// the payee account to pay. An attempt and a transfer that lost its last attempt both call it
    /// (ADR-0058 D6), so the busy answer cannot hide what an attempt refuses. Past the last attempt
    /// no lock is held, and <paramref name="underTheDaysLock"/> is false: the sum is read as it
    /// stands.
    /// </summary>
    private async Task<Account> RefuseWhatAnAttemptRefusesAsync(
        Guid userId, decimal amount, Account fromAccount, ApplicationUser recipient, Account recipientAccount,
        bool underTheDaysLock, CancellationToken cancellationToken)
    {
        /*
          The accounts come first, because the preparation reloads them on every attempt and a
          closed one then comes back with a current RowVersion (ADR-0049 D9). The payee comes before
          the funds: a payee with no open account left answers that, whatever the sender's account
          holds.
        */
        recipientAccount = await RefuseOrChooseThePayeeAccountAgainAsync(
            fromAccount, recipient, recipientAccount, cancellationToken);

        // The funds, against the balance the reload read: a debit that won the race, or one that
        // landed before a transient retry, may have moved it.
        if (fromAccount.Balance < amount)
        {
            throw new InsufficientFundsException(fromAccount.Balance, amount);
        }

        /*
          The authoritative check of the day's ceiling, under a per-user application lock taken
          before any ledger row is built (ADR-0050 D5 and its rejected alternatives). InMemory has
          no locks and skips the statement: DailyLimitConcurrencySqlServerTests is the proof.
        */
        if (underTheDaysLock && _context.Database.IsRelational())
        {
            // The values as a collection, then the token: appended to the params overload, the
            // token would be bound as a third SQL parameter, which compiles and fails at run time,
            // on SQL Server only (ADR-0058).
            await _context.Database.ExecuteSqlRawAsync(
                DailyLimitLockSql,
                new object[] { DailyLimitLockResource(userId), _dailyLimitOptions.LockTimeoutMilliseconds },
                cancellationToken);
        }

        await _dailyLimit.AssertCanMoveAsync(userId, amount, cancellationToken);

        return recipientAccount;
    }

    /// <summary>
    /// What an internal transfer looks at between the reload of its accounts and its save. A closed
    /// account of the caller's own is the ownership check's 404, and it comes before the balance
    /// because the API closes only an empty account (ADR-0049 D9).
    /// </summary>
    private void RefuseWhatTheReloadedAccountsRefuse(Account fromAccount, Account toAccount, decimal amount)
    {
        ConcurrencyRetry.RefuseIfClosed(_context, fromAccount, toAccount);

        if (fromAccount.Balance < amount)
        {
            throw new InsufficientFundsException(fromAccount.Balance, amount);
        }
    }

    /// <inheritdoc />
    public async Task<StepUpAuthorizationResponse> AuthoriseTransferAsync(
        Guid userId, TransferAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        // Ownership first, exactly as the transfer does: an unknown source account is a 404 before
        // the PIN is ever consulted, so a probe of someone else's account costs no attempt.
        await _accountAccess.GetAccountWithOwnershipCheckAsync(request.FromAccountId, userId, cancellationToken);

        var (_, recipient, _) = await ResolveExternalPayeeAsync(userId, request.RecipientAzureTag, cancellationToken);

        /*
          The ceiling comes after the payee, so the mint refuses in the transfer's order, and before
          the PIN, so a transfer the day cannot take costs no attempt (ADR-0050 D4). Not covered:
          the mint answers 404 or 422 about a handle to any signed-in caller before the PIN, bounded
          only by the BFF's per-IP limiter (ADR-0014's per-user policy covers /api/users only).
        */
        await _dailyLimit.AssertCanMoveAsync(userId, request.Amount, cancellationToken);

        var authorization = await _stepUp.MintAsync(
            userId,
            StepUpOperation.Transfer,
            new StepUpBinding(request.FromAccountId, null, recipient.Id, request.Amount),
            request.Pin,
            cancellationToken);

        return new StepUpAuthorizationResponse
        {
            AuthorizationId = authorization.Id,
            ExpiresAt = authorization.ExpiresAt
        };
    }

    /// <inheritdoc />
    public async Task<StepUpAuthorizationResponse> AuthoriseInternalTransferAsync(
        Guid userId, InternalTransferAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        await _accountAccess.GetAccountWithOwnershipCheckAsync(request.FromAccountId, userId, cancellationToken);
        await _accountAccess.GetAccountWithOwnershipCheckAsync(request.ToAccountId, userId, cancellationToken);

        if (request.FromAccountId == request.ToAccountId)
        {
            throw new BusinessRuleException("Cannot transfer to the same account.", ErrorCodes.SameAccountTransfer);
        }

        // No daily-limit check here: internal moves are not counted (ADR-0050 D2) — the money
        // stays with the user, so there is nothing the day's ceiling could bound.
        var authorization = await _stepUp.MintAsync(
            userId,
            StepUpOperation.InternalTransfer,
            new StepUpBinding(request.FromAccountId, request.ToAccountId, null, request.Amount),
            request.Pin,
            cancellationToken);

        return new StepUpAuthorizationResponse
        {
            AuthorizationId = authorization.Id,
            ExpiresAt = authorization.ExpiresAt
        };
    }

    /// <inheritdoc />
    public async Task<TransferResponse> TransferAsync(
        Guid userId, TransferRequest request, Guid? stepUpAuthorizationId,
        CancellationToken cancellationToken = default)
    {
        var fromAccount = await _accountAccess.GetAccountWithOwnershipCheckAsync(
            request.FromAccountId, userId, cancellationToken);

        /*
          Recorded here and not in RequireAuthorization, which is static: the sender's account is
          resolved by now, so the refusal row can name the account it was refused against.
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
                detail: ErrorCodes.AuthorizationRequired,
                // Never the request's token: the refusal's row must land (ADR-0044 D1, ADR-0058).
                cancellationToken: CancellationToken.None);
            throw;
        }

        var (senderUser, recipient, recipientAccount) =
            await ResolveExternalPayeeAsync(userId, request.RecipientAzureTag, cancellationToken);

        // Bound to recipient.Id, not to the handle, which can be renamed (ADR-0042 D4).
        var binding = new StepUpBinding(request.FromAccountId, null, recipient.Id, request.Amount);
        await _stepUp.ValidateAsync(userId, authorizationId, StepUpOperation.Transfer, binding, cancellationToken);

        /*
          The day's ceiling as a pre-check, outside any transaction (ADR-0050 D4): after
          ValidateAsync, so that an invalid authorisation wins, and before the balance, so that the
          mint and the transfer refuse in the same order. Two transfers from two accounts both pass
          it, which is why the authoritative check is inside the transaction.
        */
        await _dailyLimit.AssertCanMoveAsync(userId, request.Amount, cancellationToken);

        // Optimistic-concurrency conflicts on the accounts are retried (see ConcurrencyRetry).
        var strategy = _context.Database.CreateExecutionStrategy();

        for (var attempt = 1; ; attempt++)
        {
            /*
              The accounts again, before the funds, because the API closes only an empty account:
              one that closed under this transfer would answer INSUFFICIENT_FUNDS where every other
              request answers 404 (ADR-0049 D9). The payee account this returns is the one that the
              ledger row, the log line and the next reload name.
            */
            recipientAccount = await RefuseOrChooseThePayeeAccountAgainAsync(
                fromAccount, recipient, recipientAccount, cancellationToken);

            // Inside the loop: a reloaded balance may no longer cover the transfer.
            if (fromAccount.Balance < request.Amount)
            {
                throw new InsufficientFundsException(fromAccount.Balance, request.Amount);
            }

            try
            {
                return await strategy.ExecuteAsync(async ct =>
                {
                    // EnableRetryOnFailure runs this delegate again on a transient fault, against
                    // the same DbContext. Case A discards the failed attempt's tracked work, and
                    // case B never runs a committed transfer again: see
                    // PrepareTransferAttemptAsync.
                    await PrepareTransferAttemptAsync([fromAccount, recipientAccount], ct);

                    await using var dbTransaction = await _context.Database.BeginTransactionAsync(ct);

                    try
                    {
                        /*
                          Inside the transaction, because the day's sum is read under the lock it
                          holds, and after the preparation, not inside it: a transfer that committed
                          and lost its acknowledgement is answered from its claim even when an
                          account closed since (ADR-0049 D9).
                        */
                        recipientAccount = await RefuseWhatAnAttemptRefusesAsync(
                            userId, request.Amount, fromAccount, recipient, recipientAccount,
                            underTheDaysLock: true, ct);

                        var transactionNumber = IdGenerator.GenerateTransactionNumber();

                        // No timestamp is set here: UpdateTimestamps owns CreatedAt and UpdatedAt
                        // inside the save, so a value assigned here would be overwritten.
                        // ProcessedAt is read back from the saved row below, so that the API
                        // reports the instant the database holds.

                        // RelatedTransactionId stays null: two rows that reference each other are a
                        // circular foreign-key dependency that SQL Server cannot insert in one
                        // pass. The back-link is written below, inside the same transaction.
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
                            IdempotencyKey = ConcurrencyRetry.ClaimedKey(_context),
                            Status = TransactionStatus.Completed
                        };

                        // No IdempotencyKey on the payee's row: the key is the sender's handle on
                        // its own request, which the payee is never handed (ADR-0009).
                        var incomingTransaction = new Transaction
                        {
                            Id = Guid.CreateVersion7(),
                            // Its own number: a suffix on the outgoing one overflows the column,
                            // and the check symbol is right on both rows only when each number is
                            // generated (ADR-0035).
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

                        fromAccount.Balance -= request.Amount;
                        recipientAccount.Balance += request.Amount;

                        _context.Transactions.Add(outgoingTransaction);
                        _context.Transactions.Add(incomingTransaction);

                        /*
                          One audit row for the act, on the outgoing ledger row: its account is the
                          actor's, where the incoming row lands on the payee's. Detail names the
                          authorisation this transfer consumes and nothing else (ADR-0044 D5).
                        */
                        _audit.Record(
                            SecurityEvents.MoneyTransferred, AuditOutcome.Succeeded,
                            actorUserId: userId, subjectType: "Transaction",
                            subjectId: outgoingTransaction.Id,
                            detail: AuditDetails.ConsumedAuthorisation(authorizationId));
                        await _context.SaveChangesAsync(ct);

                        // Write-once back-link (permitted by the immutability
                        // guard: RelatedTransactionId null -> value only)
                        outgoingTransaction.RelatedTransactionId = incomingTransaction.Id;
                        await _context.SaveChangesAsync(ct);

                        // Spent inside the transaction and after the rows exist, so that it names
                        // the outgoing row. A second transfer on the same authorisation matches no
                        // row, and the throw rolls this one back (ADR-0042 D6).
                        if (stepUpAuthorizationId is { } toConsume)
                        {
                            await _stepUp.ConsumeAsync(userId, toConsume, outgoingTransaction.Id, ct);
                        }

                        // Through the commit gate (ADR-0058): refused if the request deadline has
                        // fired, and once started, nothing cancels it.
                        await dbTransaction.CommitAsync(ct);

                        // No amount and no handle in the log line (ADR-0017): the transaction
                        // number is the audit-trail key.
                        _logger.LogInformation(
                            "Transfer from account {FromId} to {ToId}. Transaction: {TransactionNumber}",
                            fromAccount.Id, recipientAccount.Id, transactionNumber);
                        ApiMetrics.Transfers.Add(1, new KeyValuePair<string, object?>("azurebank.kind", "external"));

                        return new TransferResponse
                        {
                            TransactionNumber = transactionNumber,
                            Amount = request.Amount,
                            NewBalance = fromAccount.Balance,
                            RecipientAzureTag = recipient.AzureTag,
                            RecipientName = $"{recipient.FirstName} {recipient.LastName[0]}.",
                            // The persisted instant: UpdateTimestamps stamped it in the first save,
                            // onto this tracked entity.
                            ProcessedAt = outgoingTransaction.CreatedAt
                        };
                    }
                    catch
                    {
                        // Keep the original fault, such as the transient the execution strategy
                        // must see to retry: a rollback on a connection that already failed can
                        // throw and would mask it. Never the request's token: a cancelled request
                        // is the one whose rollback must run.
                        try { await dbTransaction.RollbackAsync(CancellationToken.None); }
                        catch { /* best effort: the transaction may already be gone */ }
                        throw;
                    }
                }, cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex) when (ConcurrencyRetry.ShouldRetry(ex, attempt))
            {
                _logger.LogInformation(
                    "Concurrency conflict on transfer from account {AccountId} (attempt {Attempt}); retrying",
                    fromAccount.Id, attempt);
                await ConcurrencyRetry.PrepareNextAttemptAsync(
                    _context, [fromAccount, recipientAccount], cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex) when (ConcurrencyRetry.IsAccountRace(ex))
            {
                /*
                  The last attempt lost an account too, and the answer is the deposit's (ADR-0058
                  D6). It is given here and not inside the delegate, because the conflict has left
                  the execution strategy, the attempt's transaction is rolled back with the lock it
                  held, and nothing runs the delegate again. The looks an attempt takes before its
                  save run once more.
                */
                if (!await ConcurrencyRetry.ClaimIsOwnAndNothingCommittedAsync(_context, cancellationToken))
                {
                    throw;
                }

                await ConcurrencyRetry.ResetToStoreAsync(
                    _context, [fromAccount, recipientAccount], cancellationToken);
                await RefuseWhatAnAttemptRefusesAsync(
                    userId, request.Amount, fromAccount, recipient, recipientAccount,
                    underTheDaysLock: false, cancellationToken);
                await _stepUp.ValidateAsync(
                    userId, authorizationId, StepUpOperation.Transfer, binding, cancellationToken);

                _logger.LogWarning(
                    "Transfer from account {AccountId} lost the account to another write on attempt "
                        + "{Attempt}, its last; answering 503",
                    fromAccount.Id, attempt);
                throw new AccountBusyException(ex);
            }
            catch (DbUpdateException ex) when (ConcurrencyRetry.IsTransactionNumberCollision(ex, attempt))
            {
                // A regenerable clash on the transaction number: the next attempt mints a fresh
                // one. Why that is safe, and why the match is on the index name, is on
                // ConcurrencyRetry.IsTransactionNumberCollision. It is logged as a warning because
                // it should never happen: an occurrence means the number's entropy needs another
                // look.
                _logger.LogWarning(
                    ex,
                    "SecurityEvent {SecurityEvent}: transaction-number collision on transfer from "
                        + "account {AccountId} to {RecipientAccountId} (attempt {Attempt}); regenerating",
                    SecurityEvents.TransactionNumberCollision, fromAccount.Id, recipientAccount.Id, attempt);
                await ConcurrencyRetry.PrepareNextAttemptAsync(
                    _context, [fromAccount, recipientAccount], cancellationToken);
            }
        }
    }

    /// <inheritdoc />
    public async Task<InternalTransferResponse> InternalTransferAsync(
        Guid userId, InternalTransferRequest request, Guid? stepUpAuthorizationId,
        CancellationToken cancellationToken = default)
    {
        var fromAccount = await _accountAccess.GetAccountWithOwnershipCheckAsync(
            request.FromAccountId, userId, cancellationToken);
        var toAccount = await _accountAccess.GetAccountWithOwnershipCheckAsync(
            request.ToAccountId, userId, cancellationToken);

        /*
          After both ownership checks, where the external path refuses before it resolves a payee:
          the accounts here are the caller's own, so the more specific refusal withholds nothing
          (ADR-0042 D8).
        */
        // Recorded at the call site, for the reason TransferAsync gives.
        Guid authorizationId;
        try
        {
            authorizationId = RequireAuthorization(stepUpAuthorizationId);
        }
        catch (AuthenticationException)
        {
            await _audit.RecordRefusalAsync(
                SecurityEvents.MoneyTransferRefused, AuditOutcome.Refused,
                // The subject is fromAccount, the account the money would have left, as in the four
                // success events. toAccount is the nearer variable here, and the wrong one.
                actorUserId: userId, subjectType: "Account", subjectId: fromAccount.Id,
                detail: ErrorCodes.AuthorizationRequired,
                // Never the request's token: the refusal's row must land (ADR-0044 D1, ADR-0058).
                cancellationToken: CancellationToken.None);
            throw;
        }

        // Same account check (should be caught by validator, but double-check)
        if (request.FromAccountId == request.ToAccountId)
        {
            throw new BusinessRuleException("Cannot transfer to the same account.", ErrorCodes.SameAccountTransfer);
        }

        // The payee is the payer, so the binding names the destination account and no recipient
        // user. One binding for the request: the look a transfer past its attempts takes at its
        // authorisation uses it too.
        var binding = new StepUpBinding(request.FromAccountId, request.ToAccountId, null, request.Amount);
        await _stepUp.ValidateAsync(
            userId, authorizationId, StepUpOperation.InternalTransfer, binding, cancellationToken);

        // No daily-limit check on this rail, and no application lock below: internal moves are not
        // counted (ADR-0050 D2). The row this path writes carries no RecipientAzureTag, which is
        // what keeps it out of the day's sum.

        // Optimistic-concurrency conflicts on the accounts are retried (see ConcurrencyRetry).
        var strategy = _context.Database.CreateExecutionStrategy();

        for (var attempt = 1; ; attempt++)
        {
            // Both accounts again before the balance, here and after the reload inside the attempt
            // (ADR-0049 D9).
            RefuseWhatTheReloadedAccountsRefuse(fromAccount, toAccount, request.Amount);

            try
            {
                return await strategy.ExecuteAsync(async ct =>
                {
                    // As in TransferAsync: a transient fault runs this delegate again against the
                    // same DbContext, so each attempt starts from PrepareTransferAttemptAsync.
                    await PrepareTransferAttemptAsync([fromAccount, toAccount], ct);

                    // The second place: the line above reloads on every attempt, the first included.
                    RefuseWhatTheReloadedAccountsRefuse(fromAccount, toAccount, request.Amount);

                    await using var dbTransaction = await _context.Database.BeginTransactionAsync(ct);

                    try
                    {
                        var transactionNumber = IdGenerator.GenerateTransactionNumber();

                        // No timestamp is set here, as in the external transfer: UpdateTimestamps
                        // owns the stamp, and ProcessedAt is read back below.

                        // On both rows, where the external transfer puts it on one: both accounts
                        // are the caller's own.
                        var idempotencyKey = ConcurrencyRetry.ClaimedKey(_context);

                        // RelatedTransactionId stays null: rows that reference each other cannot be
                        // inserted in one pass (circular foreign key). The back-link is written
                        // below.
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
                            IdempotencyKey = idempotencyKey,
                            Status = TransactionStatus.Completed
                        };

                        var incomingTransaction = new Transaction
                        {
                            Id = Guid.CreateVersion7(),
                            // Its own number, as in the external transfer.
                            TransactionNumber = IdGenerator.GenerateTransactionNumber(),
                            AccountId = toAccount.Id,
                            Account = toAccount,
                            Type = TransactionType.TransferIn,
                            Amount = request.Amount,
                            BalanceBefore = toAccount.Balance,
                            BalanceAfter = toAccount.Balance + request.Amount,
                            Description = request.Description ?? $"Internal transfer from {fromAccount.Name}",
                            RelatedTransactionId = outgoingTransaction.Id,
                            IdempotencyKey = idempotencyKey,
                            Status = TransactionStatus.Completed
                        };

                        fromAccount.Balance -= request.Amount;
                        toAccount.Balance += request.Amount;

                        _context.Transactions.Add(outgoingTransaction);
                        _context.Transactions.Add(incomingTransaction);

                        // A different event from the external transfer's, on purpose: a move
                        // between the actor's own accounts and a payment to a third party do not
                        // weigh the same, and an evidence pack should not have to derive which it
                        // was from the ledger.
                        _audit.Record(
                            SecurityEvents.MoneyTransferredInternally, AuditOutcome.Succeeded,
                            actorUserId: userId, subjectType: "Transaction",
                            subjectId: outgoingTransaction.Id,
                            detail: AuditDetails.ConsumedAuthorisation(authorizationId));
                        await _context.SaveChangesAsync(ct);

                        // Write-once back-link (see immutability guard)
                        outgoingTransaction.RelatedTransactionId = incomingTransaction.Id;
                        await _context.SaveChangesAsync(ct);

                        // Spent inside the transaction, same as the external transfer above.
                        if (stepUpAuthorizationId is { } toConsume)
                        {
                            await _stepUp.ConsumeAsync(userId, toConsume, outgoingTransaction.Id, ct);
                        }

                        // Through the commit gate, as the external transfer's commit above.
                        await dbTransaction.CommitAsync(ct);

                        // No amount in the log line, as in the external transfer (ADR-0017).
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
                            // The persisted instant, not a copy that the save then overwrote.
                            ProcessedAt = outgoingTransaction.CreatedAt
                        };
                    }
                    catch
                    {
                        // Keep the original fault, and never the request's token, as in
                        // TransferAsync.
                        try { await dbTransaction.RollbackAsync(CancellationToken.None); }
                        catch { /* best effort: the transaction may already be gone */ }
                        throw;
                    }
                }, cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex) when (ConcurrencyRetry.ShouldRetry(ex, attempt))
            {
                _logger.LogInformation(
                    "Concurrency conflict on internal transfer from account {AccountId} (attempt {Attempt}); retrying",
                    fromAccount.Id, attempt);
                await ConcurrencyRetry.PrepareNextAttemptAsync(
                    _context, [fromAccount, toAccount], cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex) when (ConcurrencyRetry.IsAccountRace(ex))
            {
                // The last attempt lost an account too: the answer of TransferAsync at the same
                // place, by the same rule (ADR-0058 D6). Both accounts are the caller's own, so one
                // that closed meanwhile is the 404 of every other attempt.
                if (!await ConcurrencyRetry.ClaimIsOwnAndNothingCommittedAsync(_context, cancellationToken))
                {
                    throw;
                }

                await ConcurrencyRetry.ResetToStoreAsync(_context, [fromAccount, toAccount], cancellationToken);
                RefuseWhatTheReloadedAccountsRefuse(fromAccount, toAccount, request.Amount);
                await _stepUp.ValidateAsync(
                    userId, authorizationId, StepUpOperation.InternalTransfer, binding, cancellationToken);

                _logger.LogWarning(
                    "Internal transfer from account {AccountId} lost the account to another write on "
                        + "attempt {Attempt}, its last; answering 503",
                    fromAccount.Id, attempt);
                throw new AccountBusyException(ex);
            }
            catch (DbUpdateException ex) when (ConcurrencyRetry.IsTransactionNumberCollision(ex, attempt))
            {
                // A regenerable clash on the transaction number, as in TransferAsync.
                _logger.LogWarning(
                    ex,
                    "SecurityEvent {SecurityEvent}: transaction-number collision on internal transfer "
                        + "from account {AccountId} to {ToAccountId} (attempt {Attempt}); regenerating",
                    SecurityEvents.TransactionNumberCollision, fromAccount.Id, toAccount.Id, attempt);
                await ConcurrencyRetry.PrepareNextAttemptAsync(
                    _context, [fromAccount, toAccount], cancellationToken);
            }
        }
    }

    /// <summary>
    /// Prepares one more transfer attempt through
    /// <see cref="ConcurrencyRetry.PrepareIdempotentAttemptAsync"/>, which the withdrawal shares
    /// (ADR-0056). Case A: nothing committed, and the attempt starts from the store. Case B: the
    /// commit landed and its acknowledgement was lost, and the transfer is not run again.
    /// </summary>
    private Task PrepareTransferAttemptAsync(Account[] accounts, CancellationToken cancellationToken) =>
        ConcurrencyRetry.PrepareIdempotentAttemptAsync(_context, accounts, cancellationToken);
}
