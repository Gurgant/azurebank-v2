using AzureBank.Shared.Constants;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Transaction;

public class TransactionFilter
{
    public Guid? AccountId { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }

    /// <summary>
    /// An <c>Idempotency-Key</c>: lists the rows on the caller's own open accounts that the money
    /// requests sent with it wrote. A key is unique per endpoint and for 24 h only, so one key can
    /// list the rows of more than one request; a row's type and time tell them apart. Another
    /// user's key, or a key no row carries, gives an empty page. An empty page is not yet evidence
    /// that nothing was applied: while the request's claim may still be live, about 2 minutes, it
    /// can still commit (ADR-0022).
    /// </summary>
    public Guid? IdempotencyKey { get; set; }

    [Range(ValidationRules.MinPage, int.MaxValue, ErrorMessage = ValidationRules.PageRangeMessage)]
    public int Page { get; set; } = ValidationRules.DefaultPage;

    [Range(ValidationRules.MinPageSize, ValidationRules.MaxPageSize, ErrorMessage = ValidationRules.PageSizeRangeMessage)]
    public int PageSize { get; set; } = ValidationRules.DefaultPageSize;
}
