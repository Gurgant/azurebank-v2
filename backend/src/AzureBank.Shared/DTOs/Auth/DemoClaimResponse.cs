namespace AzureBank.Shared.DTOs.Auth;

/// <summary>
/// Result of a successful demo claim: the tokens and the user a login answers, and the copy the
/// visitor was given.
/// </summary>
public class DemoClaimResponse
{
    public required TokenResponse Token { get; set; }

    public required UserLoginInfo User { get; set; }

    public required DemoCopyInfo Copy { get; set; }
}

/// <summary>
/// The demo copy a visitor was given: what signs in to it again, whom it can pay, and when it ends.
/// </summary>
public class DemoCopyInfo
{
    /// <summary>The email the copy's demo user signs in with.</summary>
    public required string Email { get; set; }

    /// <summary>The demo user's password, set by this claim.</summary>
    public required string Password { get; set; }

    /// <summary>The demo user's PIN.</summary>
    public required string Pin { get; set; }

    /// <summary>The handles of the copy's other users, sorted: the payees a transfer can name.</summary>
    public required IReadOnlyList<string> Contacts { get; set; }

    /// <summary>The instant the copy ends, in UTC: the claim plus <c>Demo:CopyLifetimeHours</c>.</summary>
    public required DateTime ExpiresAt { get; set; }
}
