using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Account;
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
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        AzureBankDbContext context,
        IAccountAccessService accountAccess,
        AccountMapper mapper,
        ILogger<AccountService> logger,
        IAuditService audit)
    {
        _context = context;
        _accountAccess = accountAccess;
        _mapper = mapper;
        _logger = logger;
        _audit = audit;
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

    /// <inheritdoc />
    public async Task DeleteAccountAsync(Guid accountId, Guid userId)
    {
        var account = await _accountAccess.GetAccountWithOwnershipCheckAsync(accountId, userId);

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

        // Soft delete
        account.IsDeleted = true;
        account.DeletedAt = DateTime.UtcNow;
        account.UpdatedAt = DateTime.UtcNow;

        // Enlisted BEFORE the save, so the audit row and the soft delete are one unit: if the audit
        // insert fails, the account is not closed either (ADR-0044 D1).
        _audit.Record(
            SecurityEvents.AccountDeleted, AuditOutcome.Succeeded,
            actorUserId: userId, subjectType: "Account", subjectId: accountId);

        await _context.SaveChangesAsync();

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
          The row rides THIS SaveChanges (ADR-0044). Record only adds; the SaveChangesAsync above
          would already have flushed it — so the call goes before it, not after. Ordering matters
          here in a way it does not for the log line.
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
