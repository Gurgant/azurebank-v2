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
    public const string InvalidPin = "INVALID_PIN";
    public const string PinLocked = "PIN_LOCKED";

    // Token Authentication (JWT Bearer)
    public const string TokenMissing = "AUTH_TOKEN_MISSING";
    public const string TokenInvalid = "AUTH_TOKEN_INVALID";
    public const string TokenExpired = "AUTH_TOKEN_EXPIRED";
    public const string TokenMalformed = "AUTH_TOKEN_MALFORMED";

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
    public const string DuplicateAzureTag = "DUPLICATE_AZURE_TAG";
    public const string DuplicateEmail = "DUPLICATE_EMAIL";
    public const string SelfTransferNotAllowed = "SELF_TRANSFER_NOT_ALLOWED";
    public const string SameAccountTransfer = "SAME_ACCOUNT_TRANSFER";

    // Idempotency (monetary operations)
    public const string IdempotencyKeyMissing = "IDEMPOTENCY_KEY_MISSING";
    public const string IdempotencyKeyInvalid = "IDEMPOTENCY_KEY_INVALID";
    public const string IdempotencyKeyReuse = "IDEMPOTENCY_KEY_REUSE";
    public const string IdempotencyInFlight = "IDEMPOTENCY_IN_FLIGHT";
    public const string IdempotencyResultUnknown = "IDEMPOTENCY_RESULT_UNKNOWN";
    public const string IdempotencyPayloadTooLarge = "IDEMPOTENCY_PAYLOAD_TOO_LARGE";

    // System
    public const string InternalError = "INTERNAL_ERROR";
    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
}