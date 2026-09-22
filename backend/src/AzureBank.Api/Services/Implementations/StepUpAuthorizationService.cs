using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Services.Implementations;

/// <inheritdoc cref="IStepUpAuthorizationService" />
public class StepUpAuthorizationService : IStepUpAuthorizationService
{
    /*
      WHY THIS TAKES THE REQUEST'S OWN DbContext, unlike PinService.

      PinService keeps its own scope on purpose: its failed-attempt bookkeeping must survive a
      transfer that is about to be rolled back. This service needs the exact opposite. Consuming an
      authorisation has to ride the transfer's transaction, or the two can disagree — an
      authorisation marked spent by a transfer that rolled back is money the user can no longer send
      without re-authorising, for a payment that never happened.
    */
    private readonly AzureBankDbContext _context;
    private readonly IPinVerifier _pinVerifier;
    private readonly IAuditService _audit;
    private readonly StepUpOptions _options;
    private readonly ILogger<StepUpAuthorizationService> _logger;

    public StepUpAuthorizationService(
        AzureBankDbContext context,
        IPinVerifier pinVerifier,
        IAuditService audit,
        IOptions<StepUpOptions> options,
        ILogger<StepUpAuthorizationService> logger)
    {
        _context = context;
        _pinVerifier = pinVerifier;
        _audit = audit;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<StepUpAuthorization> MintAsync(
        Guid userId,
        StepUpOperation operation,
        StepUpBinding binding,
        string pin,
        CancellationToken cancellationToken = default)
    {
        /*
          THE ONLY PLACE A TRANSFER'S PIN IS PROVED, since ADR-0042's second half deleted
          TransferService.VerifyPinOrThrowAsync along with TransferRequest.Pin. These three checks
          were written as its deliberate mirror — same order, same exceptions — so that the two
          endpoints answered a bad PIN identically; the mirror now has one side and these are simply
          the checks. Since ADR-0049 an account closure's PIN is proved here too, through the same
          three checks.

          ~~TransactionService.WithdrawAsync still carries its own copy, and withdraw is the task
          that should converge here next.~~ (Struck 2026-09-22, ADR-0056: withdraw CONVERGED. The
          PIN left WithdrawRequest together with WithdrawAsync's IPinVerifier, and a withdrawal's
          PIN is proved here now, through TransactionService.AuthoriseWithdrawalAsync -> MintAsync.
          No copy survives anywhere: this is the only path on which any operation's PIN is proved.
          Found in review on #198 -- the PR that did the converging left the sentence asking for
          it, which is what a comment naming future work does when the future arrives.)
        */
        var user = await _context.Users.FindAsync([userId], cancellationToken);
        if (user == null)
        {
            throw new NotFoundException("User", userId);
        }

        if (string.IsNullOrEmpty(user.PinHash))
        {
            // "This operation", not "a transfer", since ADR-0049 put a closure on this rail. The
            // sentence is the 422's detail on the wire; the SPA mock aligns to the measured value.
            throw new BusinessRuleException(
                "PIN must be set before authorising this operation.", ErrorCodes.PinRequired);
        }

        // Throws 429 PIN_LOCKED when locked; false is a wrong PIN. A wrong PIN costs an attempt
        // here, and here is now the ONLY place a transfer can spend one — minting IS the
        // authentication event, so the ADR-0010 lockout lives on this endpoint rather than on the
        // transfer that used to verify in-band.
        //
        // Both outcomes are AUDITED since 2026-09-11, the way WithdrawAsync audits its own: a
        // guessed PIN is the security signal ADR-0044 kept for "the step-up path", and until then
        // it left only a log line here while the same guess on a withdrawal wrote a row. Measured
        // that day: three wrong PINs at the three mints, then three more against the lock, wrote
        // no row at all. RecordRefusalAsync, never Record: every branch here throws, so a row in
        // the caller's unit of work would be rolled back by the refusal it documents.
        bool pinOk;
        try
        {
            pinOk = await _pinVerifier.VerifyPinAsync(userId, pin);
        }
        catch (PinLockedException)
        {
            await RecordPinRefusalAsync(userId, operation, binding, ErrorCodes.PinLocked);
            throw;
        }

        if (!pinOk)
        {
            await RecordPinRefusalAsync(userId, operation, binding, ErrorCodes.InvalidPin);
            throw new AuthenticationException("Invalid PIN.", ErrorCodes.InvalidPin);
        }

        var now = DateTime.UtcNow;
        var authorization = new StepUpAuthorization
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Operation = operation,
            BindingHash = ComputeBindingHash(operation, userId, binding),
            Status = StepUpAuthorizationStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now + _options.Window
        };

        _context.StepUpAuthorizations.Add(authorization);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Step-up authorisation {AuthorizationId} minted for user {UserId} ({Operation}), expires {ExpiresAt}",
            authorization.Id, userId, operation, authorization.ExpiresAt);

        return authorization;
    }

    /// <summary>
    /// A refused PIN at a mint, on its own connection: <c>MoneyTransferRefused</c> for the two
    /// transfer mints, <c>AccountDeletionRefused</c> for the closure mint,
    /// <c>MoneyWithdrawalRefused</c> for the withdrawal mint, <c>Detail</c> the <c>ErrorCodes</c>
    /// constant the caller received.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subject is <c>binding.FromAccountId</c>, and it is safe to write because every caller
    /// has proved ownership of that account before minting: TransferService's two mints,
    /// AccountService.AuthoriseDeletionAsync and, since ADR-0056,
    /// TransactionService.AuthoriseWithdrawalAsync each call
    /// <c>GetAccountWithOwnershipCheckAsync</c> first, so the row names an account the actor owns
    /// and never one they merely named.
    /// </para>
    /// <para>
    /// ⚠️ <b>THE LIST IS THE ARGUMENT.</b> A mint missing from it is a mint nobody has checked,
    /// and the sentence above quietly stops being true — which is how it read between ADR-0056
    /// landing and the review on #198 that caught it. Prose cannot enforce that, so
    /// <c>EveryMintProvesOwnershipBeforeIt</c> in <c>SecurityEventConstantTests</c> reads the
    /// sources and fails on a fifth caller, or on one that mints before it checks.
    /// </para>
    /// </remarks>
    private Task RecordPinRefusalAsync(
        Guid userId, StepUpOperation operation, StepUpBinding binding, string errorCode) =>
        _audit.RecordRefusalAsync(
            RefusalEventFor(operation),
            AuditOutcome.Refused,
            actorUserId: userId,
            subjectType: "Account",
            subjectId: binding.FromAccountId,
            detail: errorCode);

    /*
      A SWITCH, WHERE THIS WAS A TWO-ARMED TERNARY OVER A THREE-MEMBER ENUM (ADR-0056).

      It read `operation == AccountDeletion ? AccountDeletionRefused : MoneyTransferRefused`, so
      the withdrawal added here would have fallen into the else and a refused PIN at a withdrawal
      mint would have been filed, silently and durably, as a refused TRANSFER. Nothing would have
      failed: the row writes, the caller still gets 401, and only an operator reading the trail
      months later would find a transfer that never existed.

      `_` THROWS RATHER THAN DEFAULTING. An unnamed value cast into this enum is a programming
      error and must not be given a plausible event name; and because the compiler counts unnamed
      values as reachable, an exhaustive switch cannot be made to fail the build here instead
      (TreatWarningsAsErrors is on in Release, so CS8509 WOULD be an error -- it is the enum's open
      domain, not the warning policy, that makes compile-time exhaustiveness unavailable).

      What catches a FIFTH member is therefore a test, not the compiler:
      SecurityEventConstantTests.EveryStepUpOperationMapsToItsOwnRefusalEvent walks
      Enum.GetValues<StepUpOperation>() and asserts each one maps and that no two share an event.
      Adding a member without a case here fails that test rather than mis-filing a row.
    */
    // INTERNAL rather than private so the guard named above can call it directly
    // (InternalsVisibleTo AzureBank.Tests), instead of reaching in by reflection.
    internal static string RefusalEventFor(StepUpOperation operation) => operation switch
    {
        StepUpOperation.Transfer => SecurityEvents.MoneyTransferRefused,
        StepUpOperation.InternalTransfer => SecurityEvents.MoneyTransferRefused,
        StepUpOperation.AccountDeletion => SecurityEvents.AccountDeletionRefused,
        StepUpOperation.Withdrawal => SecurityEvents.MoneyWithdrawalRefused,
        _ => throw new ArgumentOutOfRangeException(
            nameof(operation), operation, "No refusal event is defined for this step-up operation.")
    };

    /// <inheritdoc />
    public async Task ValidateAsync(
        Guid userId,
        Guid authorizationId,
        StepUpOperation operation,
        StepUpBinding binding,
        CancellationToken cancellationToken = default)
    {
        /*
          Scoped by (Id, UserId), never by Id alone. An authorisation reference is not a bearer
          token: presenting someone else's must be indistinguishable from presenting one that does
          not exist, or the endpoint becomes an oracle for which references are live.
        */
        var authorization = await _context.StepUpAuthorizations
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == authorizationId && a.UserId == userId, cancellationToken);

        if (authorization == null)
        {
            // Reason logged, never sent. Same posture as RefreshTokenService's uniform 401.
            _logger.LogWarning(
                "Step-up authorisation {AuthorizationId} not found for user {UserId}",
                authorizationId, userId);
            throw Invalid();
        }

        if (authorization.Status != StepUpAuthorizationStatus.Pending)
        {
            _logger.LogWarning(
                "Step-up authorisation {AuthorizationId} already {Status} (user {UserId})",
                authorizationId, authorization.Status, userId);
            throw Invalid();
        }

        /*
          Expiry is checked BEFORE the binding, and that order is the user-facing decision. Someone
          who waited too long with the right details should be told their confirmation expired, not
          handed the uniform "invalid" that a mismatched or forged reference gets. It leaks nothing:
          they already hold a valid authorisation of their own.
        */
        if (authorization.ExpiresAt <= DateTime.UtcNow)
        {
            _logger.LogInformation(
                "Step-up authorisation {AuthorizationId} expired at {ExpiresAt} (user {UserId})",
                authorizationId, authorization.ExpiresAt, userId);
            throw new AuthenticationException(
                "This authorisation has expired. Enter your PIN again to confirm.",
                ErrorCodes.AuthorizationExpired);
        }

        var expected = ComputeBindingHash(operation, userId, binding);
        /*
          Fixed-time comparison. The hash is not a secret the caller is guessing, but it IS derived
          from a small space — an attacker who can submit candidate authorisations and time the
          refusal should not be able to walk the binding out byte by byte. Cheap insurance against a
          class of bug that is invisible in tests.
        */
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(authorization.BindingHash),
                Encoding.ASCII.GetBytes(expected)))
        {
            _logger.LogWarning(
                "Step-up authorisation {AuthorizationId} does not match the presented operation (user {UserId})",
                authorizationId, userId);
            throw Invalid();
        }
    }

    /// <inheritdoc />
    public async Task ConsumeAsync(
        Guid userId,
        Guid authorizationId,
        Guid? consumedByTransactionId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        /*
          ONE statement, and that is the whole guarantee. Two concurrent transfers presenting the
          same authorisation both issue this UPDATE; the second blocks on the row lock, then matches
          zero rows because Status is no longer Pending. A read-then-write here would be a
          double-spend, and this repository has been bitten by exactly that shape twice — ADR-0009,
          and the PinAccessFailedCount race in ADR-0010.

          ExecuteUpdate is relational-only, so the InMemory provider (single-threaded in tests) gets
          an equivalent tracked write. The concurrency property is therefore only PROVEN against SQL
          Server, which is why its test refuses to run without AZUREBANK_TEST_SQLSERVER.
        */
        int affected;
        if (_context.Database.IsRelational())
        {
            affected = await _context.StepUpAuthorizations
                .Where(a => a.Id == authorizationId
                            && a.UserId == userId
                            && a.Status == StepUpAuthorizationStatus.Pending
                            && a.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, StepUpAuthorizationStatus.Consumed)
                    .SetProperty(a => a.ConsumedAt, now)
                    .SetProperty(a => a.ConsumedByTransactionId, consumedByTransactionId),
                    cancellationToken);
        }
        else
        {
            var authorization = await _context.StepUpAuthorizations
                .FirstOrDefaultAsync(
                    a => a.Id == authorizationId
                         && a.UserId == userId
                         && a.Status == StepUpAuthorizationStatus.Pending
                         && a.ExpiresAt > now,
                    cancellationToken);

            if (authorization == null)
            {
                affected = 0;
            }
            else
            {
                authorization.Status = StepUpAuthorizationStatus.Consumed;
                authorization.ConsumedAt = now;
                authorization.ConsumedByTransactionId = consumedByTransactionId;
                await _context.SaveChangesAsync(cancellationToken);
                affected = 1;
            }
        }

        if (affected != 1)
        {
            /*
              Reachable only by losing a race, or by an authorisation that expired between Validate
              and here. Either way nothing may be moved on it: throwing rolls back the caller's
              transaction, which is what makes "accepted once" (Art. 4(1)) true rather than intended.
            */
            _logger.LogWarning(
                "Step-up authorisation {AuthorizationId} could not be consumed by user {UserId}: " +
                "already spent, expired, or not theirs",
                authorizationId, userId);
            throw Invalid();
        }
    }

    /// <summary>
    /// HMAC-SHA256 over a versioned, delimited rendering of the operation-defining fields.
    ///
    /// <para>
    /// The <c>v1</c> prefix is not decoration: adding a bound field later must invalidate every
    /// authorisation minted under the old definition rather than silently leaving the new field
    /// unbound, and bumping the version is what makes that automatic. <c>|</c> is a safe delimiter
    /// because every part is a GUID, an invariant decimal, or an enum name — none can contain it.
    /// The amount is rendered at the stored money scale, so <c>10</c> and <c>10.0000</c> cannot mint
    /// two different bindings for the same movement.
    /// </para>
    /// </summary>
    private string ComputeBindingHash(StepUpOperation operation, Guid userId, StepUpBinding binding)
    {
        var payload = string.Join('|',
            "v1",
            operation.ToString(),
            userId.ToString("N"),
            binding.FromAccountId.ToString("N"),
            binding.ToAccountId?.ToString("N") ?? string.Empty,
            binding.RecipientUserId?.ToString("N") ?? string.Empty,
            binding.Amount.ToString($"F{ValidationRules.MoneyScale}", CultureInfo.InvariantCulture));

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.BindingKey));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static AuthenticationException Invalid() => new(
        "This authorisation cannot be used.", ErrorCodes.AuthorizationInvalid);
}
