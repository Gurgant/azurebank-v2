using System.ComponentModel.DataAnnotations;
using AzureBank.Shared.Validation;

namespace AzureBank.Shared.DTOs.User;

/// <summary>Request to rename the caller's own public AzureTag handle (ADR-0015).</summary>
public class UpdateAzureTagRequest
{
    [Required]
    [AzureTagQuery]
    public required string AzureTag { get; set; }
}
