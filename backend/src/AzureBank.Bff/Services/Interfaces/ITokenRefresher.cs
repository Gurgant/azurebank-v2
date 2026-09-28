namespace AzureBank.Bff.Services.Interfaces;

/// <summary>
/// Hands out a usable access token for a session, renewing it through the API with the session's
/// grant when it runs short (ADR-0021, 06 §4.5). The browser never sees any token; the BFF holds
/// them server-side and renews them before proxying an API call.
/// </summary>
public interface ITokenRefresher
{
    /// <summary>
    /// A token to send for the session, the reason there is none, or "try again later". Single
    /// flight per session: concurrent callers share one renewal. A renewal is never cancelled by
    /// <paramref name="cancellationToken"/>, which only stops this caller waiting for it.
    /// </summary>
    Task<AccessTokenResult> GetAccessTokenAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="ITokenRefresher.GetAccessTokenAsync"/> found.</summary>
public enum AccessTokenOutcome
{
    /// <summary>Send <see cref="AccessTokenResult.AccessToken"/>: it has not expired.</summary>
    Token,

    /// <summary>
    /// No token worth sending now, for a reason that is the service's and not the session's: a
    /// renewal failed, or was still pending after the foreground wait, and the held token has 5 s or
    /// less left. Answer 503 with <see cref="AccessTokenResult.RetryAfterSeconds"/>; the session is
    /// kept. Never forward the expired token instead: the API answers it 401, which the SPA reads as
    /// a sign-out (06 §1).
    /// </summary>
    Unavailable,

    /// <summary>
    /// No session, or it ended (its grant was refused, or it can never renew and its token expired).
    /// Send no token; the caller's answer is the one a signed-out cookie gets.
    /// </summary>
    SessionEnded,
}

/// <summary>The result of <see cref="ITokenRefresher.GetAccessTokenAsync"/>.</summary>
public sealed record AccessTokenResult(AccessTokenOutcome Outcome, string? AccessToken, int RetryAfterSeconds)
{
    /// <summary>The session has no usable token and will get none.</summary>
    public static readonly AccessTokenResult SessionEnded = new(AccessTokenOutcome.SessionEnded, null, 0);

    /// <summary>Send this token.</summary>
    public static AccessTokenResult Token(string accessToken) => new(AccessTokenOutcome.Token, accessToken, 0);

    /// <summary>Answer 503, and say when to try again.</summary>
    public static AccessTokenResult Unavailable(int retryAfterSeconds) =>
        new(AccessTokenOutcome.Unavailable, null, retryAfterSeconds);

    // An access token is a bearer credential: keep it out of any log line a record's generated
    // ToString would otherwise put it in.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Outcome = ").Append(Outcome);
        return true;
    }
}
