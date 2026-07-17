namespace AzureBank.Shared.DTOs.User;

/// <summary>The caller's AzureTag after a successful rename (ADR-0015).</summary>
public class UpdateAzureTagResponse
{
    public required string AzureTag { get; set; }
}
