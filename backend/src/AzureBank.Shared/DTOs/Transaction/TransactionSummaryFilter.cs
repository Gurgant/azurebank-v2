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
    // Kept to ONE line of XML doc on purpose: this comment reaches the published contract, where
    // the sibling date parameters carry no description at all. The first draft was a three-part
    // `<para>` essay, and the generator flattened it into 759 characters of run-together prose with
    // literal CRLFs in the middle — visible in the spec diff. The reasoning belongs here, in a
    // comment the generator never reads:
    //
    //   * Same name and shape as `TransactionFilter.AccountId` because the two are read TOGETHER.
    //     Scoping the ledger to one account while the summary stays global gives totals that
    //     disagree with the rows beneath them — which the dashboard shipped apologising for, for
    //     exactly as long as this parameter did not exist.
    //   * An id the caller does not own is a 403, matching the list. Not a 404 and not an empty
    //     summary: either would answer "does this account exist?" differently depending on the
    //     answer, which is an enumeration oracle over other people's account ids.

    /// <summary>
    /// Optional single-account scope; omitted, the summary covers every account the caller owns.
    /// </summary>
    public Guid? AccountId { get; set; }

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
