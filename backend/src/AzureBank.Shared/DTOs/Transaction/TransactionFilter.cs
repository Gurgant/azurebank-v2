using AzureBank.Shared.Constants;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Transaction;

public class TransactionFilter
{
    public Guid? AccountId { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }

    [Range(ValidationRules.MinPage, int.MaxValue, ErrorMessage = ValidationRules.PageRangeMessage)]
    public int Page { get; set; } = ValidationRules.DefaultPage;

    [Range(ValidationRules.MinPageSize, ValidationRules.MaxPageSize, ErrorMessage = ValidationRules.PageSizeRangeMessage)]
    public int PageSize { get; set; } = ValidationRules.DefaultPageSize;
}
