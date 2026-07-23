namespace AzureBank.Shared.DTOs.Transaction;

/// <summary>
/// Aggregated money movement over a date window, computed server-side (SQL SUM) and
/// scoped to the caller's accounts. Amounts are unsigned and direction comes from the
/// transaction type: income = Deposit + TransferIn, expenses = Withdrawal + TransferOut.
/// Only Completed transactions count toward the sums — Pending/Failed/Reversed must not
/// inflate money totals; in-flight items surface separately via <see cref="PendingCount"/>.
/// </summary>
public class TransactionSummaryResponse
{
    /// <summary>Sum of Completed Deposit + TransferIn amounts inside the window.</summary>
    public decimal TotalIncome { get; set; }

    /// <summary>Sum of Completed Withdrawal + TransferOut amounts inside the window.</summary>
    public decimal TotalExpenses { get; set; }

    /// <summary><see cref="TotalIncome"/> minus <see cref="TotalExpenses"/>.</summary>
    public decimal NetChange { get; set; }

    /// <summary>Count of Pending transactions inside the window.</summary>
    public int PendingCount { get; set; }

    /// <summary>Resolved inclusive window start (echoes the applied default when omitted).</summary>
    public DateTime FromDate { get; set; }

    /// <summary>Resolved inclusive window end (echoes the applied default when omitted).</summary>
    public DateTime ToDate { get; set; }
}
