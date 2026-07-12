using AzureBank.Shared.Validation;
using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Auth;

public class VerifyPinRequest
{
    [Required]
    [Pin]
    public required string Pin { get; set; }
}
