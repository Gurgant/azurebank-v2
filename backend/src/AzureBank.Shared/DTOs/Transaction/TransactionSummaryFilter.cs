using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Transaction;

/// <summary>
/// Optional inclusive date window for the transaction summary.
/// Omitted bounds default server-side to the current UTC calendar month
/// (FromDate = first day of the month, ToDate = now); the resolved window is
/// echoed back in <see cref="TransactionSummaryResponse"/>.
/// </summary>
public class TransactionSummaryFilter : IValidatableObject
{
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }

    /// <summary>
    /// Cross-field rule for the explicitly-provided pair; the service re-checks the
    /// RESOLVED window (a lone future FromDate against the defaulted ToDate).
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FromDate.HasValue && ToDate.HasValue && FromDate.Value > ToDate.Value)
        {
            yield return new ValidationResult(
                "FromDate must be earlier than or equal to ToDate.",
                [nameof(FromDate), nameof(ToDate)]);
        }
    }
}
