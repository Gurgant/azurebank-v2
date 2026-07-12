namespace AzureBank.Shared.Constants;

/// <summary>
/// Centralized validation rules - single source of truth
/// Used by: DataAnnotations, FluentValidation, Frontend validation
/// </summary>
public static class ValidationRules
{
    // ═══════════════════════════════════════════════════════════════
    // PASSWORD
    // ═══════════════════════════════════════════════════════════════
    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 128;
    // Password requirements (aligned with ASP.NET Core Identity defaults):
    // - At least one lowercase letter
    // - At least one uppercase letter
    // - At least one digit
    // - At least one special character (non-alphanumeric)
    // - At least 4 unique characters (enforced by Identity, not regex)
    // Length {8,128} embedded in pattern because Schemathesis ignores minLength when pattern exists
    public const string PasswordPattern = @"^(?=.*[a-z])(?=.*[A-Z])(?=.*[0-9])(?=.*[^a-zA-Z0-9])[\x20-\x7E]{8,128}$";
    public const string PasswordPatternMessage = "Password must contain at least one uppercase, one lowercase, one digit, and one special character.";

    // ═══════════════════════════════════════════════════════════════
    // AZURE TAG
    // ═══════════════════════════════════════════════════════════════
    public const int AzureTagMinLength = 3;
    public const int AzureTagMaxLength = 20;
    // Length {2,19} + first char = 3-20 total, embedded because Schemathesis ignores minLength when pattern exists
    public const string AzureTagPattern = @"^[a-z][a-z0-9_]{2,19}$"; // Must start with letter
    public const string AzureTagPatternMessage = "AzureTag must start with a letter and contain only lowercase letters, numbers, and underscores.";

    // ═══════════════════════════════════════════════════════════════
    // PIN
    // ═══════════════════════════════════════════════════════════════
    public const int PinLength = 6;
    // Use [0-9] instead of \d to avoid matching Unicode digits (e.g., Arabic ٠-٩)
    // This ensures Schemathesis generates only ASCII digits for contract testing
    public const string PinPattern = @"^[0-9]{6}$";
    public const string PinPatternMessage = "PIN must be exactly 6 digits.";

    // ═══════════════════════════════════════════════════════════════
    // EMAIL
    // ═══════════════════════════════════════════════════════════════
    public const int EmailMaxLength = 255;

    // ═══════════════════════════════════════════════════════════════
    // NAME FIELDS
    // ═══════════════════════════════════════════════════════════════
    public const int FirstNameMinLength = 2;
    public const int FirstNameMaxLength = 50;
    public const string FirstNameLengthMessage = "First name must be between 2 and 50 characters.";
    public const int LastNameMinLength = 2;
    public const int LastNameMaxLength = 50;
    public const string LastNameLengthMessage = "Last name must be between 2 and 50 characters.";
    // Use explicit character ranges instead of \p{L} to avoid Unicode matching issues
    // Allows ASCII letters, common accented chars, spaces, hyphens, apostrophes
    // Length {2,50} embedded because Schemathesis ignores minLength when pattern exists
    public const string NamePattern = @"^[a-zA-ZÀ-ÖØ-öø-ÿ\s'-]{2,50}$";
    public const string NamePatternMessage = "Name can only contain letters, spaces, hyphens, and apostrophes.";

    // ═══════════════════════════════════════════════════════════════
    // ACCOUNT
    // ═══════════════════════════════════════════════════════════════
    public const int AccountNameMinLength = 2;
    public const int AccountNameMaxLength = 100;
    public const string AccountNameLengthMessage = "Account name must be between 2 and 100 characters.";
    public const int AccountNumberLength = 15;  // AB-XXXX-XXXX-XX format (15 chars)
    public const string AccountNumberPattern = @"^AB-\d{4}-\d{4}-\d{2}$";
    public const string AccountNumberPatternMessage = "Account number must be in the format AB-XXXX-XXXX-XX.";
    public const string AccountNotEmptyGuid = "A valid account ID is required.";
    public const int AccountTypeMaxLength = 20;  // Enum stored as string (e.g., "Investment")

    // ═══════════════════════════════════════════════════════════════
    // MONEY / TRANSACTIONS
    // ═══════════════════════════════════════════════════════════════
    public const decimal TransactionMinAmount = 0.01m;
    public const decimal TransactionMaxAmount = 100_000.00m;  // €100k per transaction
    public const decimal DailyTransferLimit = 1_000.00m;      // €1k daily limit (standard user)
    public const int MoneyDecimalPlaces = 2;

    // Database precision for DECIMAL type - DECIMAL(19,4)
    public const int MoneyPrecision = 19;   // Total digits (max ~922 trillion)
    public const int MoneyScale = 4;        // Decimal places (sub-cent precision)

    public const int TransactionNumberLength = 20;    // TXN-YYYYMMDD-XXXXXX format
    public const int TransactionDescriptionMaxLength = 500;
    public const string DescriptionMaxLengthMessage = "Description cannot exceed 500 characters.";
    public const int TransactionTypeMaxLength = 20;   // Enum stored as string (e.g., "TransferOut")
    public const int TransactionStatusMaxLength = 20; // Enum stored as string (e.g., "Completed")

    // ═══════════════════════════════════════════════════════════════
    // PAGINATION
    // ═══════════════════════════════════════════════════════════════
    public const int DefaultPageSize = 20;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;
    public const int DefaultPage = 1;
    public const int MinPage = 1;
    public const string PageRangeMessage = "Page must be at least 1.";
    public const string PageSizeRangeMessage = "PageSize must be between 1 and 100.";

    // ═══════════════════════════════════════════════════════════════
    // SECURITY TRACKING & STORAGE
    // ═══════════════════════════════════════════════════════════════
    public const int IpAddressMaxLength = 45;    // IPv6 max
    public const int UserAgentMaxLength = 500;
    public const int TokenHashLength = 44;       // Base64 of SHA256
    public const int PinHashMaxLength = 200;     // Argon2id hash storage
}
