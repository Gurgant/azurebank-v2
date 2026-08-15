using AzureBank.Api.Mappers;
using AzureBank.Api.Observability;
using AzureBank.Api.Security;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.User;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Services.Interfaces;
using AzureBank.Shared.Utilities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.Compliance.Redaction;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// Authentication service handling login, registration, and PIN operations.
/// Uses ASP.NET Core Identity for user management.
/// </summary>
public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AzureBankDbContext _context;
    private readonly IJwtService _jwtService;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IPinVerifier _pinVerifier;
    private readonly UserMapper _userMapper;
    private readonly AccountMapper _accountMapper;
    private readonly ILoginTimingEqualizer _timingEqualizer;
    private readonly ILogger<AuthService> _logger;
    private readonly Redactor _piiRedactor;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        AzureBankDbContext context,
        IJwtService jwtService,
        IRefreshTokenService refreshTokenService,
        IPasswordHasher passwordHasher,
        IPinVerifier pinVerifier,
        UserMapper userMapper,
        AccountMapper accountMapper,
        ILoginTimingEqualizer timingEqualizer,
        ILogger<AuthService> logger,
        IRedactorProvider redactorProvider)
    {
        _userManager = userManager;
        _context = context;
        _jwtService = jwtService;
        _refreshTokenService = refreshTokenService;
        _passwordHasher = passwordHasher;
        _pinVerifier = pinVerifier;
        _userMapper = userMapper;
        _accountMapper = accountMapper;
        _timingEqualizer = timingEqualizer;
        _logger = logger;
        // Resolve by CLASSIFICATION, not by concrete type: this service only declares that
        // it logs PII; which masking strategy applies is the observability layer's decision
        // (see AddRedaction in ObservabilityServiceCollectionExtensions). Resolved once —
        // redactors are stateless singletons.
        _piiRedactor = redactorProvider.GetRedactor(new DataClassificationSet(DataClassifications.Pii));
    }

    /// <inheritdoc />
    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // Spend the same DOMINANT (PBKDF2) password-hash cost a real account would, so
            // an unknown email can't be told apart by that latency; the response body is
            // already identical to a wrong password. A smaller write-latency residual on
            // the account-exists path remains (ADR-0012) — bounded by upstream rate limiting.
            _timingEqualizer.SpendVerifyCost(request.Password);
            // Unknown account, so there is no stable user id to log — mask the email (PII)
            // instead of dropping it: logs are exported over OTLP, and "j***@example.com"
            // still lets an operator correlate a credential-stuffing burst.
            _logger.LogWarning("Failed login attempt for email {Email}", _piiRedactor.Redact(request.Email));
            ApiMetrics.Logins.Add(1, new KeyValuePair<string, object?>("azurebank.outcome", "failed"));
            throw new AuthenticationException("Invalid email or password.");
        }

        var now = DateTimeOffset.UtcNow;
        var passwordOk = await _userManager.CheckPasswordAsync(user, request.Password);
        // Respect Identity's per-user LockoutEnabled opt-out (matches IsLockedOutAsync):
        // an exempt account (e.g. a service account) is never treated as locked.
        var lockedUntil = user.LockoutEnabled && user.LockoutEnd is { } end && end > now
            ? end
            : (DateTimeOffset?)null;

        if (passwordOk)
        {
            // Correct password on a locked account: reveal the lock ONLY here — the
            // caller has proven knowledge of the password, so the signal carries no
            // enumeration value — and give a precise Retry-After (ADR-0012).
            if (lockedUntil is { } until)
            {
                _logger.LogWarning("Login refused for locked account {UserId} until {Until}", user.Id, until);
                ApiMetrics.Logins.Add(1, new KeyValuePair<string, object?>("azurebank.outcome", "locked"));
                throw AccountLockedException.Until(until, now);
            }

            // Success: clear any accumulated failures / expired lock.
            if (user.AccessFailedCount != 0 || user.LockoutEnd is not null)
            {
                await ResetLoginLockoutAsync(user);
            }

            var tokenResult = _jwtService.GenerateToken(user);
            var refreshToken = await _refreshTokenService.IssueAsync(user);
            _logger.LogInformation("User {UserId} logged in successfully", user.Id);
            ApiMetrics.Logins.Add(1, new KeyValuePair<string, object?>("azurebank.outcome", "succeeded"));
            return new LoginResponse
            {
                Token = tokenResult.AccessToken,
                ExpiresAt = tokenResult.ExpiresAt, // single source of truth (the token's exp)
                RefreshToken = refreshToken,
                User = _userMapper.ToLoginInfo(user)
            };
        }

        // Wrong password. Count it toward the lockout (but not while already locked, so
        // a persistent attacker cannot extend the window), then return the SAME generic
        // 401 as an unknown user — the lock state is never leaked to a password guesser.
        if (lockedUntil is null)
        {
            await IncrementAndMaybeLockLoginAsync(user, now);
        }
        // Wrong password on a KNOWN account: log the stable user id, not the raw email (PII).
        _logger.LogWarning("Failed login attempt for account {UserId}", user.Id);
        ApiMetrics.Logins.Add(1, new KeyValuePair<string, object?>("azurebank.outcome", "failed"));
        throw new AuthenticationException("Invalid email or password.");
    }

    // ---- Atomic login-lockout writers (Identity's native AccessFailedCount / LockoutEnd) ----
    // A single set-based ExecuteUpdate (no read-modify-write, so no lost updates under a
    // burst of parallel wrong passwords). Identity's UserManager.AccessFailedAsync would
    // instead do an optimistic-concurrency read-modify-write whose lost update EF silently
    // folds into IdentityResult.ConcurrencyFailure — letting a parallel burst stay under
    // the threshold and never lock. ExecuteUpdate is relational-only, so the InMemory test
    // provider falls back to an equivalent tracked write (single-threaded there).

    /// <summary>
    /// One atomic failed-login transition: increment AccessFailedCount, and when it crosses
    /// <see cref="ValidationRules.MaxLoginAttempts"/> set LockoutEnd and reset the counter —
    /// all against the row's CURRENT value in a single statement. An EXPIRED lock is cleared;
    /// a FUTURE lock is never cleared, so the threshold cannot be bypassed by parallel bursts.
    /// </summary>
    private async Task IncrementAndMaybeLockLoginAsync(ApplicationUser user, DateTimeOffset now)
    {
        // An account exempt from lockout (LockoutEnabled=false) never accrues lock state,
        // preserving the invariant "LockoutEnd non-null => the account was lockout-eligible"
        // that the read gate relies on. (Diverges from Identity's AccessFailedAsync, which
        // always increments; safe here because the read gate never re-checks the flag mid-count.)
        if (!user.LockoutEnabled)
            return;

        var max = ValidationRules.MaxLoginAttempts;
        var until = now.AddMinutes(ValidationRules.LoginLockoutMinutes);

        if (_context.Database.IsRelational())
        {
            // The WHERE excludes a currently-locked (or exempt) row, so a late concurrent
            // increment that lands AFTER a peer already latched the lock updates zero rows —
            // no residual count survives onto the next window. Because a matched row is
            // therefore always null-or-expired, the LockoutEnd else-branch is just null.
            await _context.Users
                .Where(u => u.Id == user.Id && u.LockoutEnabled && (u.LockoutEnd == null || u.LockoutEnd < now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.AccessFailedCount,
                        u => u.AccessFailedCount + 1 >= max ? 0 : u.AccessFailedCount + 1)
                    .SetProperty(u => u.LockoutEnd,
                        u => u.AccessFailedCount + 1 >= max ? (DateTimeOffset?)until : null)
                    .SetProperty(u => u.UpdatedAt, (DateTime?)DateTime.UtcNow));
            // Detach so the tracked (now-stale) user can't be written back by a later save.
            _context.Entry(user).State = EntityState.Detached;
            return;
        }

        if (user.AccessFailedCount + 1 >= max)
        {
            user.AccessFailedCount = 0;   // the lock window is now authoritative
            user.LockoutEnd = until;
        }
        else
        {
            user.AccessFailedCount += 1;
            if (user.LockoutEnd is { } expired && expired < now)
            {
                user.LockoutEnd = null;
            }
        }
        user.UpdatedAt = DateTime.UtcNow;   // parity with the relational writer's audit bump
        await _context.SaveChangesAsync();
    }

    // On the relational path ExecuteUpdate bypasses the change tracker, so the tracked
    // `user` keeps its stale AccessFailedCount/LockoutEnd. Detach it so a later
    // SaveChanges in the same request (e.g. a future unit-of-work or audit interceptor)
    // can't write those stale values back and silently revert the reset. Subsequent reads
    // (JWT generation, identity mapping) work fine on the detached entity.
    private async Task ResetLoginLockoutAsync(ApplicationUser user)
    {
        if (_context.Database.IsRelational())
        {
            await _context.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.AccessFailedCount, 0)
                    .SetProperty(u => u.LockoutEnd, (DateTimeOffset?)null)
                    .SetProperty(u => u.UpdatedAt, (DateTime?)DateTime.UtcNow));
            _context.Entry(user).State = EntityState.Detached;
            return;
        }

        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.UpdatedAt = DateTime.UtcNow;   // parity with the relational writer's audit bump
        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<RegisterResponse> RegisterAsync(RegisterRequest request)
    {
        // Reject duplicates with a single enumeration-NEUTRAL response so an anonymous
        // caller can't read which field (email or handle) collided — the specific reason
        // is logged server-side only, as a structured SecurityEvent an operator can alert
        // on (ADR-0013). This is defense-in-depth: it removes the plaintext label, but it
        // does NOT close the structural oracle (a duplicate returns 409 while a fresh email
        // returns 201 + a token). Full closure needs the deferred email-confirmation flow.
        var normalizedAzureTag = request.AzureTag.ToLower();
        if (await _userManager.FindByEmailAsync(request.Email) != null)
        {
            // Email masked (PII — exported over OTLP); the masked form is still enough for
            // an operator to spot a targeted enumeration probe against one address.
            _logger.LogWarning(
                "SecurityEvent {SecurityEvent}: registration rejected, email already registered ({Email})",
                SecurityEvents.DuplicateRegistration, _piiRedactor.Redact(request.Email));
            throw new ConflictException("Registration could not be completed.", ErrorCodes.RegistrationFailed);
        }
        if (await _context.Users.AnyAsync(u => u.AzureTag == normalizedAzureTag))
        {
            /*
              Sanitized for the LOG only — never for the query above, which must match the value
              the user actually claimed.

              The handle is already pattern-validated (ValidationRules.AzureTagPattern is anchored,
              ^[a-z][a-z0-9_]{2,19}$, so it cannot carry CR/LF), and that is exactly the argument
              this site was dismissed on twice. Twice is the point: the alert reopens every time the
              line moves, and the argument depends on a validator in another file continuing to run
              on every path that reaches here. Routing through the audited barrier costs one local
              and settles it — the same reasoning, and the same helper, as AccountService's
              CreateAccountAsync.
            */
            var safeAzureTag = LogSanitizer.Sanitize(normalizedAzureTag);
            _logger.LogWarning(
                "SecurityEvent {SecurityEvent}: registration rejected, handle already taken ({AzureTag})",
                SecurityEvents.DuplicateRegistration, safeAzureTag);
            throw new ConflictException("Registration could not be completed.", ErrorCodes.RegistrationFailed);
        }

        /*
          ALL OR NOTHING. Everything registration writes — the Identity user, its role, and the
          starter account — commits together or not at all.

          Before this, UserManager.CreateAsync committed the user in its own unit of work, so any
          failure at the account INSERT left a user holding the email and the AzureTag with no
          account and no way back: the pre-checks above would then find their own row and return the
          neutral 409 forever. ADR-0036 fixed the one cause it could (a duplicate account number,
          still retried below) and recorded the rest as open. Measured with a non-collision fault:
          500 to the client, user committed, role assigned, zero accounts, 409 on every retry.

          Identity is registered with AddEntityFrameworkStores<AzureBankDbContext>, so UserManager
          writes through this same scoped context and enlists in the transaction begun here — which
          is the whole reason this works without touching UserStore.AutoSaveChanges.

          Run through the execution strategy because EnableRetryOnFailure is on and EF refuses a
          user-initiated transaction under a retrying strategy otherwise. The change tracker is
          cleared and the entities rebuilt INSIDE the delegate: a rolled-back attempt leaves them
          tracked, and a retry that reused them would try to insert the previous attempt's rows
          alongside the new ones.

          Exceptions are deliberately allowed to escape the delegate. ConflictException from the
          duplicate paths is not transient, so the strategy does not retry it; the transaction
          disposes unconmitted and the caller still gets the enumeration-neutral 409 of ADR-0013,
          which ARealDuplicateStillGetsTheNeutralConflict pins.
        */
        ApplicationUser user = null!;
        Account account = null!;

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(async () =>
        {
            _context.ChangeTracker.Clear();

            // Decouple the login identity from the public handle (ADR-0015): Identity's UserName
            // is the immutable user id (a UUIDv7 — time-sortable, index-friendly), never shown and
            // never a login credential (login is by email), so the AzureTag is left as a plain,
            // renameable public column. Set the Id explicitly so UserName can mirror it here.
            var userId = Guid.CreateVersion7();
            user = new ApplicationUser
            {
                Id = userId,
                UserName = userId.ToString(),
                Email = request.Email,
                AzureTag = normalizedAzureTag,
                FirstName = request.FirstName.Trim(),
                LastName = request.LastName.Trim(),
                EmailConfirmed = true // Skip email verification for MVP
            };

            IdentityResult result;
            try
            {
                result = await _userManager.CreateAsync(user, request.Password);
            }
            catch (DbUpdateException ex) when (ConcurrencyRetry.IsRegistrationDuplicate(ex))
            {
                // The genuine TOCTOU race: a concurrent registration passed the advisory
                // pre-checks and Identity's validators too, then a unique index (AzureTag, or now
                // the NormalizedEmail unique index) rejected this one at write time. Neutralise it
                // to the SAME response as a pre-check duplicate so the race can't be used to
                // enumerate accounts (ADR-0013).
                //
                // NARROWED BY INDEX NAME, and load-bearing now that this catch sits inside the
                // execution-strategy delegate. DbUpdateException is EF's generic wrapper for
                // anything that fails in the update pipeline — deadlock 1205, lock timeout 1222,
                // connectivity 10053/40613, command timeout -2, a truncation or FK defect. Every one
                // of those used to be reported to the caller as "these details are taken", and since
                // ADR-0037 it would ALSO rob the strategy of the retry it performs on a transient:
                // it decides by walking the inner exception chain, and ConflictException has none.
                //
                // The same file already narrows the identical catch sixty lines below
                // (IsAccountNumberCollision), so until now a deadlock on the ACCOUNT insert was
                // retried while the very same deadlock on the USER insert became a 409.
                _logger.LogWarning(ex,
                    "SecurityEvent {SecurityEvent}: registration lost the unique-index race",
                    SecurityEvents.DuplicateRegistration);
                throw new ConflictException("Registration could not be completed.", ErrorCodes.RegistrationFailed);
            }

            if (!result.Succeeded)
            {
                // Never echo Identity's error descriptions to the client. A duplicate that slips
                // past the advisory pre-checks (a race) surfaces here as a Duplicate* code; it
                // must return the SAME neutral 409 as the pre-check path or the differing
                // response re-opens the enumeration oracle (ADR-0013). Branch on the stable
                // error Code, not the localisable Description. Any other failure gets a generic
                // message (it is not an existence oracle).
                var codes = string.Join(",", result.Errors.Select(e => e.Code));
                var isDuplicate = result.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail");
                _logger.LogWarning(
                    "SecurityEvent {SecurityEvent}: registration rejected by Identity ({Codes})",
                    isDuplicate ? SecurityEvents.DuplicateRegistration : SecurityEvents.RegistrationRejected,
                    codes);
                if (isDuplicate)
                {
                    throw new ConflictException("Registration could not be completed.", ErrorCodes.RegistrationFailed);
                }
                throw new BusinessRuleException("Registration could not be completed.");
            }

            /*
              The role result is CHECKED, not discarded. This transaction's whole claim is that the
              user, the role and the account commit together (ADR-0037), and a dropped role is the
              one partial commit that could still return 201.

              NOT a BusinessRuleException: that maps to 422, which would report an operator/seeding
              fault as a defect in the caller's payload. A rollback to 500 matches what the
              missing-role case already does — UserStore throws InvalidOperationException
              "Role USER does not exist." — which is why RoleSeeder must run before the first
              registration.

              Duplicate* routes to the neutral 409 for the same reason as the CreateAsync branch
              above: AddToRoleAsync re-runs the user validators, and a duplicate surfacing here must
              not become a status that only ever appears when the email exists (ADR-0013).
            */
            var roleResult = await _userManager.AddToRoleAsync(user, Roles.Default);
            if (!roleResult.Succeeded)
            {
                var roleCodes = string.Join(",", roleResult.Errors.Select(e => e.Code));
                if (roleResult.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail"))
                {
                    _logger.LogWarning(
                        "SecurityEvent {SecurityEvent}: registration lost the race during role assignment",
                        SecurityEvents.DuplicateRegistration);
                    throw new ConflictException(
                        "Registration could not be completed.", ErrorCodes.RegistrationFailed);
                }

                _logger.LogError(
                    "Default role assignment failed for user {UserId} ({Codes}); rolling registration back",
                    user.Id, roleCodes);
                throw new InvalidOperationException($"Failed to assign default role ({roleCodes}).");
            }

            // Create default primary account
            account = new Account
            {
                UserId = user.Id,
                AccountNumber = IdGenerator.GenerateAccountNumber(),
                Name = "Primary Account",
                Type = AccountType.Checking,
                Balance = 0,
                IsPrimary = true,
                User = user
            };

            _context.Accounts.Add(account);

            /*
              Retried on an account-number collision. HISTORY, because the reason changed under it:
              CreateAsync used to commit the user in its own unit of work, so an uncaught duplicate
              here left a user holding the email and the AzureTag with no account and no way back —
              measured as 500, user committed, role assigned, zero accounts, 409 on every retry
              (ADR-0036). The transaction opened above now rolls all of that back, so this retry is
              kept for a DIFFERENT reason: it turns a recoverable clash into a success rather than a
              rollback the caller has to redo (ADR-0037).

              Narrowed by index name inside the predicate, which is load-bearing here: this same request
              can legitimately lose the AzureTag or NormalizedEmail race, and THAT must stay the
              enumeration-neutral 409 (ADR-0013) rather than become a retry loop.
            */
            await ConcurrencyRetry.SaveNewAccountAsync(_context, account, _logger, user.Id);
        },
        /*
          THE AMBIGUOUS COMMIT. EF owns the transaction now, and this asks whether it landed rather
          than assuming it did not.

          Without it, a transient raised BY the commit — a dropped connection, a failover, a bare
          TimeoutException, all of which EnableRetryOnFailure retries — re-runs the delegate against
          a database that ALREADY holds this registration. The pre-checks live outside the delegate
          so they do not re-run; Identity finds the committed row, answers DuplicateEmail, and a
          caller whose registration SUCCEEDED gets the neutral 409 with no token and no account.
          The same shape PR #93 fixed in the seeder.

          Invoked ONLY for a commit-phase exception: EF sets its CommitFailed flag in the statement
          immediately before CommitAsync and short-circuits on it, so anything thrown from inside
          the delegate — including every non-duplicate DbUpdateException that now propagates past the
          narrowed catch above — skips this and goes straight to the retry decision. By the time it
          runs, `user` is always assigned.

          KEYED ON THE UUIDv7 MINTED FOR THIS ATTEMPT, never on the email or the AzureTag. Either of
          those could be satisfied by a row a CONCURRENT registration won, which would hand this
          caller a 201 and a JWT for somebody else's user. The user row alone suffices because the
          entire point of the transaction is that the account cannot exist without it.
        */
        async () => user is not null && await _context.Users.AnyAsync(u => u.Id == user.Id));


        var tokenResult = _jwtService.GenerateToken(user);

        // The transaction above has COMMITTED by this point, so the user and the account are
        // durable. Issuing the refresh token is a best-effort convenience on top of that: if this
        // write fails, do NOT fail the whole registration — that would 500 the client for an account
        // that WAS created and then hand back a confusing duplicate-409 on retry. The user still
        // gets an access token now and a refresh token on their next login.
        //
        // Deliberately OUTSIDE the transaction, and the reason inverted with ADR-0037. It used to be
        // that enrolling it changed nothing, because the duplicate-409 came from an already-committed
        // Identity user. Now it would change something, and the wrong thing: a failed token write
        // would roll back a registration that had otherwise succeeded. Best-effort is the point.
        string? refreshToken = null;
        try
        {
            refreshToken = await _refreshTokenService.IssueAsync(user);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Refresh-token issuance failed after registering user {UserId}; continuing without it", user.Id);
        }

        _logger.LogInformation("User {UserId} registered successfully with account {AccountId}", user.Id, account.Id);

        return new RegisterResponse
        {
            User = _userMapper.ToLoginInfo(user),
            Account = _accountMapper.ToResponse(account),
            Token = new Shared.DTOs.Auth.TokenResponse
            {
                AccessToken = tokenResult.AccessToken,
                RefreshToken = refreshToken,
                ExpiresIn = Math.Max(0, (int)(tokenResult.ExpiresAt - DateTime.UtcNow).TotalSeconds),
                TokenType = "Bearer",
                ExpiresAt = tokenResult.ExpiresAt
            }
        };
    }

    /// <inheritdoc />
    public async Task<UserResponse> GetCurrentUserAsync(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());

        if (user == null)
        {
            throw new NotFoundException("User", userId);
        }

        return _userMapper.ToResponse(user);
    }

    /// <inheritdoc />
    public async Task<RefreshResponse> RefreshAsync(RefreshRequest request)
    {
        // Rotate first (revoke the presented token, mint a successor + detect reuse); the
        // returned user lets us mint the matching access token without a second lookup.
        var rotation = await _refreshTokenService.RotateAsync(request.RefreshToken);
        var tokenResult = _jwtService.GenerateToken(rotation.User);

        _logger.LogInformation("Refreshed access token for user {UserId}", rotation.User.Id);

        return new RefreshResponse
        {
            AccessToken = tokenResult.AccessToken,
            RefreshToken = rotation.RefreshToken,
            ExpiresAt = tokenResult.ExpiresAt // single source of truth (the token's exp)
        };
    }

    /// <inheritdoc />
    public async Task LogoutAsync(Guid userId)
    {
        // Revoke every active refresh token so a logout genuinely ends the session's ability
        // to re-mint access tokens (a stolen-but-not-yet-rotated token is neutralised too).
        await _refreshTokenService.RevokeAllForUserAsync(userId);
        _logger.LogInformation("User {UserId} logged out", userId);
    }

    /// <inheritdoc />
    public Task<bool> VerifyPinAsync(Guid userId, string pin) =>
        // Delegated to PinService (attempt-limiting + lockout persisted in its own
        // DbContext scope, so it never rides the caller's transaction/idempotency).
        _pinVerifier.VerifyPinAsync(userId, pin);

    /// <inheritdoc />
    public async Task SetPinAsync(Guid userId, SetPinRequest request)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());

        if (user == null)
        {
            throw new NotFoundException("User", userId);
        }

        // Read BEFORE the hash below is overwritten — afterwards every enrolment looks like a change.
        var enrolling = string.IsNullOrEmpty(user.PinHash);

        /*
          Each transition costs a proof the SESSION cannot supply. Changing costs the old PIN;
          enrolling costs the password, because there is no old PIN to ask for.

          This assignment used to be unconditional, which made the whole step-up story decorative:
          a caller holding only a session could overwrite the PIN and then pass every gate that
          PIN protects. Measured end to end through the BFF, cookie only, before this guard:

            register            -> 201, authLevel 1
            set-pin "131313"    -> 200   (enrolment)
            set-pin "999999"    -> 200   <- no proof of "131313" asked for
            verify-pin "999999" -> 200,  authLevel 2
            GET .../full-number -> 200   "AB-3142-8079-89", unmasked

          ADR-0010's attempt-limiting never engaged, because nothing was guessed. And ADR-0008's
          step-up gate cannot help: it checks that A PIN was entered, not that the entered PIN was
          the user's. The check has to live here, at the point of replacement.

          Verified through IPinVerifier rather than the hasher directly so a wrong CurrentPin
          counts against the SAME lockout as every other wrong PIN — otherwise this endpoint would
          be an uncounted brute-force oracle, which is strictly worse than what it replaces.
        */
        if (string.IsNullOrEmpty(user.PinHash))
        {
            /*
              ENROLLING. The mirror of the change branch below, and it closes the half ADR-0040 left
              open on purpose. Its deferral said the reasoning was "worth re-examining if the
              enrolment entry point ever moves away from the post-registration handoff" — #105 moved
              it: there are now four ways into /pin-setup and three of them can be days after login.

              Measured through the BFF on `main` @ 4811667, before this branch existed:
                register                    -> 201, hasPin:false, authLevel 1
                set-pin "424242"            -> 200      cookie ONLY, no password
                verify-pin "424242"         -> 200, authLevel 2
                deposit 250 / withdraw 250  -> 201, balanceAfter 0.0000
                GET .../full-number         -> 200, "AB-8512-2148-18"
              A session cookie was the entire proof required to mint the credential that authorises
              every money movement.

              The password is the bar and also the CEILING: NIST SP 800-63-4B §4.1.2 requires binding
              at the maximum AAL currently available on the account or the maximum at which the new
              authenticator will be used, WHICHEVER IS LOWER. With no PIN enrolled, the account's
              maximum is the password — so demanding anything heavier would be invention, not rigour.
            */
            if (string.IsNullOrEmpty(request.Password))
            {
                // 422 for the same reason as the CurrentPin branch: "required only when no PIN
                // exists yet" is a business rule the schema cannot express.
                throw new BusinessRuleException(
                    "Your password is required to set a PIN.", ErrorCodes.PasswordRequired);
            }

            await VerifyAccountPasswordAsync(user, request.Password);
        }
        else
        {
            if (string.IsNullOrEmpty(request.CurrentPin))
            {
                // 422, not 400: "required only when a PIN already exists" is a business rule the
                // schema cannot express, which is the split BusinessRuleException documents.
                throw new BusinessRuleException(
                    "The current PIN is required to change it.", ErrorCodes.PinRequired);
            }

            // Throws PinLockedException (429) if locked; false on a wrong PIN under the threshold.
            if (!await _pinVerifier.VerifyPinAsync(userId, request.CurrentPin))
            {
                // Same shape withdraw returns for a bad PIN (TransactionService.WithdrawAsync).
                throw new AuthenticationException("Invalid PIN.", ErrorCodes.InvalidPin);
            }

            /*
              MIRROR the reset onto the TRACKED entity, or UpdateAsync below undoes it.

              A successful verification clears the failure counters — but PinService does that in
              its OWN DbContext (ResetLockoutAsync, via ExecuteUpdate on the relational path), while
              `user` here was loaded by _userManager and still carries the pre-verification values.
              `UpdateAsync(user)` writes the whole tracked row, so those stale counters go straight
              back over the reset.

              Observed before this fix, black-box and with no database access: fail twice, change
              the PIN with the correct current one, then fail once more — that attempt answered 429
              instead of 401, i.e. it was counted as the third rather than the first. The reset had
              been resurrected.

              Same shape as the aliasing that bit this codebase in InMemoryTokenStore: two views of
              one row, one of them stale, and the stale one wins because it is written last.
            */
            user.PinAccessFailedCount = 0;
            user.PinLockoutEnd = null;
        }

        user.PinHash = _passwordHasher.HashPin(request.Pin);
        var result = await _userManager.UpdateAsync(user);

        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new BusinessRuleException($"Failed to set PIN: {errors}");
        }

        if (enrolling)
        {
            // Detective control, in the OPERATOR's log — see SecurityEvents.PinEnrolled: this is
            // deliberately NOT the independent notification to the account owner that NIST
            // SP 800-63-4B §4.1.2 also requires, because there is no mail transport in this system
            // to send one with. Saying so here rather than letting the line read as compliance.
            _logger.LogInformation(
                "SecurityEvent {SecurityEvent}: user {UserId} enrolled a PIN after proving their password",
                SecurityEvents.PinEnrolled, userId);
        }

        _logger.LogInformation("User {UserId} set their PIN", userId);
    }

    /// <summary>
    /// Prove the account password for a sensitive action that is NOT a login, on the SAME lockout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counting is the whole point, and skipping it would have been strictly worse than the hole
    /// this closes: an endpoint that checks a password without counting failures is an UNCOUNTED
    /// PASSWORD-GUESSING ORACLE, reachable with nothing but a session. The same sentence is already
    /// written a few lines above about <c>CurrentPin</c>, which is verified through
    /// <c>IPinVerifier</c> precisely so wrong values land on the shared PIN lockout.
    /// </para>
    /// <para>
    /// Every branch mirrors <see cref="LoginAsync"/>, on purpose — including revealing a lock ONLY
    /// once the password has been proven (ADR-0012), so the lock state never becomes a signal for a
    /// guesser. What it does NOT mirror is the generic "Invalid email or password": the caller is
    /// already authenticated as a known account, so there is no enumeration to protect and a vague
    /// message would only leave the user guessing which field was wrong.
    /// </para>
    /// </remarks>
    private async Task VerifyAccountPasswordAsync(ApplicationUser user, string password)
    {
        var now = DateTimeOffset.UtcNow;
        var passwordOk = await _userManager.CheckPasswordAsync(user, password);
        // Respect Identity's per-user LockoutEnabled opt-out, exactly as the login path does.
        var lockedUntil = user.LockoutEnabled && user.LockoutEnd is { } end && end > now
            ? end
            : (DateTimeOffset?)null;

        if (passwordOk)
        {
            if (lockedUntil is { } until)
            {
                _logger.LogWarning(
                    "PIN enrolment refused for locked account {UserId} until {Until}", user.Id, until);
                throw AccountLockedException.Until(until, now);
            }

            if (user.AccessFailedCount != 0 || user.LockoutEnd is not null)
            {
                await ResetLoginLockoutAsync(user);
                /*
                  MIRROR the reset onto the TRACKED entity — the same aliasing trap the CurrentPin
                  branch documents. ResetLoginLockoutAsync writes through its own path; `user` here
                  was loaded by _userManager and still holds the pre-reset counters, and the
                  UpdateAsync that persists the new PIN hash writes the whole tracked row. Without
                  these two lines the reset is resurrected by the very call that stores the PIN.
                */
                user.AccessFailedCount = 0;
                user.LockoutEnd = null;
            }

            return;
        }

        // Wrong password. Count it — but not while already locked, so a persistent attacker cannot
        // extend the window by hammering this endpoint instead of the login one.
        if (lockedUntil is null)
        {
            await IncrementAndMaybeLockLoginAsync(user, now);
        }

        _logger.LogWarning("PIN enrolment refused: wrong password for account {UserId}", user.Id);
        throw new AuthenticationException("Invalid password.", ErrorCodes.InvalidCredentials);
    }
}
