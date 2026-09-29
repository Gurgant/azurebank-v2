using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 503: the request could not be served now, and sending it again later is the right answer.
/// Carries a <c>retryAfterSeconds</c> detail, which <c>AppExceptionHandler</c> also writes as the
/// <c>Retry-After</c> header.
/// </summary>
/// <remarks>
/// Raised where the caller's retry is the recovery and a 500 would read as "stop": today
/// <c>POST /api/auth/revoke</c> when its database write fails, so the BFF's revoker keeps the grant
/// queued and tries again (ADR-0057 §4.4, RFC 7009 §2.2.1).
/// </remarks>
public class ServiceUnavailableException : AppException
{
    /// <summary>
    /// The <c>Retry-After</c> of the outage 503 (ADR-0058): the database cannot be reached, or the
    /// request ran past its deadline. EF's longest wait between two retries, so a client that comes
    /// back after it meets a database EF would have tried again by then. For machines: a visitor is
    /// told no time.
    /// </summary>
    public const int OutageRetryAfterSeconds = 10;

    public ServiceUnavailableException(string message, int retryAfterSeconds)
        : base(message, ErrorCodes.ServiceUnavailable, 503)
    {
        Details = new Dictionary<string, object> { ["retryAfterSeconds"] = retryAfterSeconds };
    }
}
