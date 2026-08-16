namespace AzureBank.Shared.Constants;

/// <summary>
/// Standardized error codes for API responses
/// </summary>
public static class ErrorCodes
{
    // Authentication
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string SessionExpired = "SESSION_EXPIRED";
    public const string PinRequired = "PIN_REQUIRED";

    /// <summary>
    /// The account password is required for this action. Today: ENROLLING a PIN, where there is no
    /// previous PIN to ask for and the session alone is not a proof of identity (NIST SP 800-63-4B
    /// §4.1.2 — bind an authenticator behind authentication, not behind a session).
    /// </summary>
    public const string PasswordRequired = "PASSWORD_REQUIRED";
    public const string InvalidPin = "INVALID_PIN";
    public const string PinLocked = "PIN_LOCKED";

    // Step-up authorisation (ADR-0042). Three codes because the user-facing sentences differ, and
    // before this the difference could not be said at all: an elapsed elevation and one that was
    // never granted were the same 403 STEP_UP_REQUIRED from AuthLevelMiddleware.

    /// <summary>
    /// The operation needs an authorisation and none was presented. "You have not authorised this
    /// yet" — distinct from having authorised and run out of time.
    /// </summary>
    public const string AuthorizationRequired = "AUTHORIZATION_REQUIRED";

    /// <summary>
    /// The authorisation was valid and its window has passed. Nothing was written and no PIN attempt
    /// was spent — an expiry is not a failed authentication, and must never count towards the lock.
    /// The client re-prompts for the PIN; the amount and payee stay on screen (WCAG 2.2 SC 3.3.7
    /// Redundant Entry, Level A: only the security information may be asked for again).
    /// </summary>
    public const string AuthorizationExpired = "AUTHORIZATION_EXPIRED";

    /// <summary>
    /// Deliberately UNIFORM across unknown, not-yours, already-spent, and bound-to-different-data.
    /// Same reasoning as <see cref="RefreshTokenInvalid"/>: the specific reason is logged server-side
    /// and never put on the wire, so the response is not an oracle for whether a given authorisation
    /// reference exists or who owns it.
    /// </summary>
    public const string AuthorizationInvalid = "AUTHORIZATION_INVALID";

    // Token Authentication (JWT Bearer)
    public const string TokenMissing = "AUTH_TOKEN_MISSING";
    public const string TokenInvalid = "AUTH_TOKEN_INVALID";
    public const string TokenExpired = "AUTH_TOKEN_EXPIRED";
    public const string TokenMalformed = "AUTH_TOKEN_MALFORMED";
    // Refresh-token rotation (POST /api/auth/refresh). Deliberately UNIFORM across every
    // failure — unknown, expired, or reuse-detected — so the response is not an oracle for
    // WHY a refresh was rejected (the specific reason is logged server-side; ADR-0021).
    public const string RefreshTokenInvalid = "REFRESH_TOKEN_INVALID";

    // Authorization
    public const string AccessDenied = "ACCESS_DENIED";
    public const string InsufficientPermissions = "INSUFFICIENT_PERMISSIONS";
    public const string Forbidden = "AUTH_FORBIDDEN";

    // Validation
    public const string ValidationError = "VALIDATION_ERROR";
    public const string InvalidRequest = "INVALID_REQUEST";

    // Business Rules
    public const string InsufficientFunds = "INSUFFICIENT_FUNDS";
    public const string AccountNotFound = "ACCOUNT_NOT_FOUND";
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string TransactionNotFound = "TRANSACTION_NOT_FOUND";
    // Client-facing, enumeration-neutral code for a rejected self-registration: a duplicate
    // email and a duplicate handle are indistinguishable to the caller, and the specific
    // reason is logged server-side only (ADR-0013). This replaced the former
    // DUPLICATE_EMAIL / DUPLICATE_AZURE_TAG codes, which are gone — nothing emits them.
    public const string RegistrationFailed = "REGISTRATION_FAILED";
    // A handle rename collided with an existing AzureTag. Unlike registration (where the
    // response is enumeration-neutral), telling a signed-in user their chosen handle is taken
    // is fine: the exact-match lookup already confirms handle existence (ADR-0014/0015).
    public const string AzureTagTaken = "AZURE_TAG_TAKEN";
    public const string SelfTransferNotAllowed = "SELF_TRANSFER_NOT_ALLOWED";
    // Declared, thrown, and NOT reachable over HTTP. InternalTransferTests pins why:
    // InternalTransferRequestValidator's NotEqual rule rejects from == to during model validation,
    // which runs before the action, so the wire answer is a 400 with an errors dictionary and this
    // 422 never leaves the building. The service check behind it is deliberate defence in depth for
    // any non-HTTP caller — the code says so — and must NOT be deleted as dead. What must not
    // happen is a client being written to expect this code from the API.
    public const string SameAccountTransfer = "SAME_ACCOUNT_TRANSFER";
    // The recipient exists but has no account that can receive money (all deleted). Reachable, and
    // currently unhandled by the frontend, which falls through to the generic message — tracked for
    // the U6 frontend work rather than papered over here.
    public const string RecipientNoAccount = "RECIPIENT_NO_ACCOUNT";

    // Account lifecycle
    public const string NonZeroBalance = "NON_ZERO_BALANCE";
    public const string PrimaryAccountDelete = "PRIMARY_ACCOUNT_DELETE";

    // Query validation
    public const string InvalidDateRange = "INVALID_DATE_RANGE";

    // Idempotency (monetary operations)
    public const string IdempotencyKeyMissing = "IDEMPOTENCY_KEY_MISSING";
    public const string IdempotencyKeyInvalid = "IDEMPOTENCY_KEY_INVALID";
    public const string IdempotencyKeyReuse = "IDEMPOTENCY_KEY_REUSE";
    public const string IdempotencyInFlight = "IDEMPOTENCY_IN_FLIGHT";
    public const string IdempotencyResultUnknown = "IDEMPOTENCY_RESULT_UNKNOWN";
    public const string IdempotencyPayloadTooLarge = "IDEMPOTENCY_PAYLOAD_TOO_LARGE";

    // Exception defaults. These are the fallback codes AppException subclasses apply when a
    // thrower does not name one, and they go on the wire like any other — so they belong here
    // rather than inline in the exception's own signature, where ErrorCodeConstantTests could
    // not see them and a second copy could drift.
    public const string BusinessRuleViolation = "BUSINESS_RULE_VIOLATION";
    public const string Conflict = "CONFLICT";

    // System
    public const string InternalError = "INTERNAL_ERROR";
    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
}