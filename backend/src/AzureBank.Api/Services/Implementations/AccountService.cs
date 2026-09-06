using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Utilities;
using Microsoft.EntityFrameworkCore;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Enums;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// Account management service handling CRUD operations and balance queries.
/// </summary>
public class AccountService : IAccountService
{
    private readonly AzureBankDbContext _context;
    private readonly IAccountAccessService _accountAccess;
    private readonly AccountMapper _mapper;
    private readonly IAuditService _audit;
    private readonly IStepUpAuthorizationService _stepUp;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        AzureBankDbContext context,
        IAccountAccessService accountAccess,
        AccountMapper mapper,
        ILogger<AccountService> logger,
        IAuditService audit,
        IStepUpAuthorizationService stepUp)
    {
        _context = context;
        _accountAccess = accountAccess;
        _mapper = mapper;
        _logger = logger;
        _audit = audit;
        _stepUp = stepUp;
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
        await ConcurrencyRetry.SaveNewAccountAsync(_context, account, _logger, userId);

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

    /// <summary>
    /// The two closure guards, in the order they have always run: balance first, then primary.
    /// Shared by the mint and the deletion so an authorisation can never be minted for a closure
    /// the deletion then refuses — the same "cannot drift" argument the transfer mints make by
    /// resolving the payee at mint time.
    /// </summary>
    private static void RefuseIfNotClosable(Account account)
    {
        // Business rules: cannot delete if balance is non-zero
        if (account.Balance != 0)
        {
            // The balance is the caller's OWN account and they already have it from /api/accounts,
            // so naming it here adds nothing and used to render it in the server process culture.
            throw new BusinessRuleException(
                "Cannot delete an account with a non-zero balance.",
                ErrorCodes.NonZeroBalance);
        }

        // Business rules: cannot delete primary account
        if (account.IsPrimary)
        {
            throw new BusinessRuleException(
                "Cannot delete primary account. Set another account as primary first.",
                ErrorCodes.PrimaryAccountDelete);
        }
    }

    /// <inheritdoc />
    public async Task<StepUpAuthorizationResponse> AuthoriseDeletionAsync(
        Guid userId, Guid accountId, string pin)
    {
        // Ownership first, exactly as the transfer mints do: an unknown or foreign account is a
        // 404/403 before the PIN is ever consulted, so a probe of someone else's account costs no
        // attempt. Then the two guards, for the same reason — a closure that cannot happen must not
        // be a cheaper PIN oracle than one that can.
        var account = await _accountAccess.GetAccountWithOwnershipCheckAsync(accountId, userId);
        RefuseIfNotClosable(account);

        var authorization = await _stepUp.MintAsync(
            userId,
            StepUpOperation.AccountDeletion,
            StepUpBinding.ForAccountDeletion(accountId),
            pin);

        return new StepUpAuthorizationResponse
        {
            AuthorizationId = authorization.Id,
            ExpiresAt = authorization.ExpiresAt
        };
    }

    /// <inheritdoc />
    public async Task DeleteAccountAsync(Guid accountId, Guid userId, Guid? stepUpAuthorizationId)
    {
        var account = await _accountAccess.GetAccountWithOwnershipCheckAsync(accountId, userId);

        /*
          THE GUARDS STAY AHEAD OF THE PRESENCE CHECK, and that is a deliberate departure from
          ADR-0042, which put the transfer's refusal at the ownership rung so that a caller holding
          no second factor could learn nothing further down. It does not apply here: the two guards
          reveal only the caller's OWN account state, which GET /api/accounts already hands them,
          so there is no oracle to protect. What the order DOES protect is the real-stack pin in
          the SPA's contract suite — a headerless DELETE on a funded account answers 422
          NON_ZERO_BALANCE, and moving the presence check above the guards would turn that 401 from
          a backend-only change. Do not reorder (ADR-0049).
        */
        RefuseIfNotClosable(account);

        /*
          RECORDED AT THE CALL SITE, after the ownership check, so the refusal names the account it
          was refused against — the placement TransferService uses, and for the same reason. On its
          OWN connection (RecordRefusalAsync), because this request writes nothing else: there is no
          transaction for the row to ride, and the throw below is the whole outcome.

          An EMPTY Step-Up-Authorization header binds to null exactly like an absent one, so both
          land here; a header that is present but not a UUID never reaches this method — MVC model
          binding refuses it upstream with a 400 keyed on the header name (measured on the transfer
          endpoints, ADR-0042, and on this one 2026-09-06T10:44Z — D4 in
          measure-after-2026-09-06.txt: 400 model-state keyed "Step-Up-Authorization").
        */
        if (stepUpAuthorizationId is not { } authorizationId)
        {
            await _audit.RecordRefusalAsync(
                SecurityEvents.AccountDeletionRefused, AuditOutcome.Refused,
                actorUserId: userId, subjectType: "Account", subjectId: accountId,
                detail: ErrorCodes.AuthorizationRequired);
            throw new AuthenticationException(
                "This account closure has not been authorised.", ErrorCodes.AuthorizationRequired);
        }

        // Before any write, so an expired or mismatched authorisation costs nothing. The binding
        // is the same factory the mint used: a transfer authorisation minted from this very account
        // hashes differently (the operation name is in the payload) and is refused as INVALID.
        await _stepUp.ValidateAsync(
            userId, authorizationId, StepUpOperation.AccountDeletion,
            StepUpBinding.ForAccountDeletion(accountId));

        /*
          AN EXPLICIT TRANSACTION, WHERE THIS METHOD USED TO HAVE NONE. The soft delete and its
          AccountDeleted row already committed as one unit — the row rides the same SaveChanges
          (ADR-0044 D1) — but ConsumeAsync is a separate ExecuteUpdate that, with no caller
          transaction, would autocommit beside that save: a closure could then commit while its
          authorisation stayed Pending, or the reverse. So the transaction is opened here, through
          the execution strategy (EnableRetryOnFailure refuses a user-initiated transaction outside
          one; measured on transfers, AzureBankDbContext says the same), and with it present the
          DbContext funnel applies the audit chain INSIDE it rather than opening its own.

          CONSUME AFTER THE SAVE, NOT BEFORE. ConsumeAsync throws when its UPDATE matches zero rows —
          an authorisation spent by a concurrent request, or one that expired between Validate and
          here — and the throw is what rolls the soft delete back. Consuming first would spend the
          authorisation and then let the save fail on its own, which is the disagreement the
          transaction exists to prevent. AccountDeletionSqlServerTests holds this order with a
          rollback proof.
        */
        var strategy = _context.Database.CreateExecutionStrategy();

        /*
          TWO RETRY LOOPS, ONE INSIDE THE OTHER, as in TransferService. The execution strategy
          re-runs the delegate on a TRANSIENT fault; the loop around it re-runs the attempt when the
          account's RowVersion refuses a stale write (DbUpdateConcurrencyException) — a deposit that
          landed between the guard and the save, or a second DELETE presenting this same
          authorisation that committed first. Without the outer loop those losers surfaced as an
          unmapped concurrency exception, which is a 500: the first version of the eight-way proof
          answered one 200 and seven 500s (the test's own output, the implementer's ~10:20Z run on
          2026-09-06, filed in the working-state repo as
          plans/account-deletion/measure-tests-2026-09-06.txt — a test run, not a running-stack
          measurement). Bounded by ConcurrencyRetry.MaxAttempts, jittered between attempts, and the
          last failure propagates.
        */
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await strategy.ExecuteAsync(async () =>
                {
                    /*
                      RE-ENTRANCY DISCIPLINE, at the top of the delegate because EnableRetryOnFailure
                      re-runs the whole delegate on a transient fault against this SAME DbContext, and
                      the outer loop re-enters it too. Each attempt has to rebuild its state from the
                      store, or it would find the account already flagged deleted in memory and a
                      second Added AuditEvent still tracked from the attempt that failed — one closure,
                      two rows. ResetToStoreAsync is the discard TransferService's
                      PrepareTransferAttemptAsync performs (its Case A): it detaches every tracked
                      AuditEvent and reloads the account, which resets IsDeleted, DeletedAt and
                      UpdatedAt to what the database holds.

                      A CLOSURE THAT ALREADY HAPPENED ANSWERS AS ANY LATER DELETE DOES: 404. If the
                      reload comes back deleted, either an earlier attempt of this same request
                      committed and only its acknowledgement was lost, or a concurrent request
                      presenting the same authorisation won the row; the delegate cannot tell the two
                      apart and does not try. In both the closure, its row and the spend are in the
                      database, so writing them again would double the row or be refused at the
                      consume — and the ownership rung at the top of this method answers a deleted
                      account with 404, so this branch answers the same. The reload SEES the closed
                      row despite the soft-delete filter: EntityEntry.Reload/GetDatabaseValues
                      queries the store without the global query filter, so the entry comes back
                      Unchanged with IsDeleted = true rather than detached (measured 2026-09-06 on
                      SQL Server LocalDB and InMemory, EF Core 10.0.1, both for a first-pass loser
                      whose flag was never set and for a retry whose own attempt had set it). There
                      is no "no row" branch; the check below reads the store's flag. An earlier
                      version of this note described a detached-entry branch the provider does not
                      take.

                      AND THE GUARDS AGAIN. A reload that shows the account open but funded — the
                      deposit that raced the first attempt — must not be closed on the strength of a
                      check made against the old balance. AccountDeletionSqlServerTests
                      .ADepositThatRacesTheClosure_IsRefusedOnTheRetry_AndSpendsNothing drives that
                      race on the real provider; with the call below deleted it answered 200 and
                      closed the funded account (mutated and restored 2026-09-06: 422
                      NON_ZERO_BALANCE, IsDeleted=False, Balance=5, authorisation Pending).
                    */
                    await ConcurrencyRetry.ResetToStoreAsync(_context, account);
                    if (account.IsDeleted)
                    {
                        throw new NotFoundException("Account", accountId);
                    }

                    RefuseIfNotClosable(account);

                    await using var dbTransaction = await _context.Database.BeginTransactionAsync();

                    try
                    {
                        // Soft delete. DeletedAt is stamped here and UpdatedAt again by UpdateTimestamps
                        // inside SaveChanges, so the two can differ by microseconds; folding DeletedAt into
                        // the context's clock is a TimeProvider decision ADR-0049 leaves open.
                        account.IsDeleted = true;
                        account.DeletedAt = DateTime.UtcNow;
                        account.UpdatedAt = DateTime.UtcNow;

                        // Enlisted BEFORE the save, so the audit row and the soft delete are one unit: if
                        // the audit insert fails, the account is not closed either (ADR-0044 D1). Detail
                        // stays null, as on every success row (ADR-0044 D5); the authorisation that paid
                        // for the closure is found by the operator query ADR-0049 records.
                        _audit.Record(
                            SecurityEvents.AccountDeleted, AuditOutcome.Succeeded,
                            actorUserId: userId, subjectType: "Account", subjectId: accountId);

                        await _context.SaveChangesAsync();

                        // Spent inside this transaction and after the rows exist, as the transfers do. A
                        // closure produces no ledger row, so consumed-by is null rather than a value the
                        // evidence verb's join on movement id could never match.
                        await _stepUp.ConsumeAsync(userId, authorizationId, consumedByTransactionId: null);

                        await dbTransaction.CommitAsync();
                    }
                    catch
                    {
                        // Preserve the ORIGINAL fault (e.g. the transient the execution strategy must see
                        // to retry): rolling back a transaction whose connection/commit already failed can
                        // itself throw and would otherwise mask it. Same shape as TransferService.
                        try { await dbTransaction.RollbackAsync(); }
                        catch { /* best effort: the transaction may already be gone */ }
                        throw;
                    }
                });

                break;
            }
            catch (DbUpdateConcurrencyException ex) when (ConcurrencyRetry.ShouldRetry(ex, attempt))
            {
                // The RowVersion moved under this attempt. Reload, jitter, and go round: the top
                // of the delegate decides whether there is still a closure to make.
                await ConcurrencyRetry.PrepareNextAttemptAsync(_context, account);
            }
        }

        /*
          A SecurityEvent, not a plain LogInformation, and the asymmetry it corrects is the argument:
          AccountNumberRevealed — reading your own account number back — was on the operator's alert
          stream, and closing an account was not. The acting user is named because the event is
          useless for audit without it, and because a deleted account cannot be queried afterwards
          to find out whose it was: the global query filter hides it from every read path, so this
          line is the only record that survives in the log.

          THE TWO IDENTIFIERS STAY IN CLEAR, for the reasons spelled out at
          GetFullAccountNumberAsync below — this note exists so the next reader does not re-derive
          them. CodeQL raised cs/cleartext-storage on this exact line as alert #34 (high) the first
          time it shipped, and the automated suggestion was again to log a SHA-256 of the accountId.
          It was refused again, and the ninth time is not a new judgement: alerts #16-#20, #23, #25
          and #31 are the same rule dismissed as a false positive on this same file, and #25 and #31
          record the hashing suggestion being rejected by name.

          The rule's heuristic keys on the identifier NAME containing "account"; the value is a
          UUIDv7 surrogate key, returned to its owner by GET /api/accounts and present in this very
          request's URL. Not a credential, not PII. The sensitive value here would be the account
          NUMBER, which AccountMapper masks server-side and which never reaches this logger.

          Hashing protects nothing, and an earlier version of this note gave the wrong reason for
          that — it claimed a hash "could not be joined back" because the row is soft-deleted behind
          the global query filter. FALSE, and worth keeping as a correction: nothing purges these
          rows, so anyone holding the database hashes every account id once and has a complete
          reverse-lookup table. The search space is the ROW COUNT, not 2^122 — the attacker never
          inverts the digest (WP29 WP216; EDPB 01/2025 on hashing as pseudonymisation, not
          anonymisation). The EF filter is an application-correctness convenience, not a boundary
          on SQL.

          The true reason is simpler. A hash would only help a reader holding the LOGS but not the
          DATABASE, and this system has no such reader: the only always-on sink is the console, and
          the optional collector is a loopback Grafana on the same host as the database. Serilog
          also logs the request path unconditionally in both the API and the BFF, so the raw id is
          already in this request's own log output — hashing this one field would remove one copy
          of three and change nothing.

          Positively, this is also what the standards ask for: NIST SP 800-53 AU-3(f) requires the
          audit record to identify the objects associated with the event, PCI DSS v4 10.2.2 the
          identity of the affected resource, and OWASP's Logging Cheat Sheet gives "user database
          table primary key-value" as its first example of a correct identity field. Masking is
          scoped to secrets, PAN and descriptive PII. See ADR-0017 ("log the opaque id, not PII");
          pseudonymising one site while twenty others log ids in clear is task #206, and it is
          decided: no, for the reason above — there is no trust boundary to buy anything with.
        */
        /*
          The row rides the SaveChanges inside the transaction above (ADR-0044). Record only adds;
          that SaveChangesAsync would already have flushed it — so the call goes before it, not
          after. Ordering matters there in a way it does not for this log line, which is why the
          line sits outside the transaction: it describes a closure that has committed.
        */
        _logger.LogInformation(
            "SecurityEvent {SecurityEvent}: user {UserId} deleted account {AccountId}",
            SecurityEvents.AccountDeleted, userId, accountId);
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

        /*
          Detective audit line (SecurityEvent series): WHO revealed WHICH account — never the number
          itself. PII redaction is opt-in per call site, so the value must not enter the logging
          pipeline at all.

          THE TWO IDENTIFIERS STAY IN CLEAR, and that is the control, not an oversight.
          CodeQL raises cs/cleartext-storage here (twice now: dismissed as alert #25, reopened the
          moment this line moved), and the automated suggestion is to log SHA-256 prefixes of both
          Guids instead. That must not be applied: hashing them makes the record un-joinable to the
          accounts and users tables, so the line stops answering the only question it exists to
          answer — who revealed which account. A control that cannot be correlated is not a weaker
          control, it is a decoration.

          They are also not sensitive. Both are opaque surrogate keys: not credentials, not PII, and
          useless to anyone without the database they index. The sensitive value in this method is
          the account NUMBER, and it is deliberately absent from the message above.
        */
        _logger.LogInformation(
            "SecurityEvent {SecurityEvent}: user {UserId} revealed the full account number of account {AccountId}",
            SecurityEvents.AccountNumberRevealed, userId, accountId);

        /*
          A read has no SaveChanges of its own to ride, so this one is saved here explicitly. It is
          still Succeeded rather than a refusal: the number WAS returned, and that is the fact the
          detective control of ADR-0020 exists to record.
        */
        _audit.Record(
            SecurityEvents.AccountNumberRevealed, AuditOutcome.Succeeded,
            actorUserId: userId, subjectType: "Account", subjectId: accountId);
        await _context.SaveChangesAsync();

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
