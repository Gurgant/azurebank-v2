namespace AzureBank.Shared.Enums;


/// <summary>
/// Transaction Statuses: Pending, Completed, Failed, Reversed
/// </summary>
/// <remarks>
/// Unspecified (-1) is a sentinel value for EF Core - never persisted to database.
/// When entity is created with Unspecified, the database default (Completed) is applied.
/// </remarks>
public enum TransactionStatus
{
    /// <summary>
    /// Sentinel value for EF Core - indicates status was not explicitly set.
    /// Database will apply default value (Completed).
    /// </summary>
    Unspecified = -1,

    Pending = 0,
    Completed = 1,
    Failed = 2,
    Reversed = 3
}
