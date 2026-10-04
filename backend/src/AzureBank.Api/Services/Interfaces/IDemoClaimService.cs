using AzureBank.Shared.DTOs.Auth;

namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Hands a free demo copy to a visitor of the public demo.
/// </summary>
public interface IDemoClaimService
{
    /// <summary>
    /// Takes one free copy for the visitor at <c>request.ClientAddress</c>, in one transaction: the
    /// copy is marked claimed, its owner is given a password, and a grant is issued for a session
    /// as that owner. Answers the tokens and the user a login answers, and what signs in to the
    /// copy again.
    /// </summary>
    /// <exception cref="Shared.Exceptions.DemoRefusalException">
    /// 429: no copy is free, or this client has claimed as many copies as one client may in a day.
    /// Nothing is written.
    /// </exception>
    /// <exception cref="InvalidOperationException">The demo is off (<c>Demo:Enabled</c>).</exception>
    Task<DemoClaimResponse> ClaimAsync(DemoClaimRequest request, CancellationToken cancellationToken = default);
}
