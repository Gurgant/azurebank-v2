using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.DTOs.Auth;

/// <summary>
/// Request body for POST /api/auth/demo/claim: the BFF asks for a free demo copy for the visitor
/// it is serving.
/// </summary>
public class DemoClaimRequest
{
    /// <summary>The most characters <see cref="ClientAddress"/> may have.</summary>
    public const int MaxClientAddressLength = 64;

    /// <summary>
    /// The visitor's address as the BFF saw it. The copies one client may claim in a day are
    /// counted by it.
    /// </summary>
    [Required]
    [StringLength(MaxClientAddressLength, MinimumLength = 1)]
    public required string ClientAddress { get; set; }
}
