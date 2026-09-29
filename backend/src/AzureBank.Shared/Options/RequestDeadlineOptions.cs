using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.Options;

/// <summary>
/// How long the API lets one request run before it gives up on it (ADR-0058). Binds to the
/// "RequestDeadline" section; validated at start.
/// </summary>
/// <remarks>
/// <para>
/// When the deadline fires, the request's cancellation token is cancelled: a command waiting on the
/// database is cancelled, and the request answers 503 <c>SERVICE_UNAVAILABLE</c>. It never cancels a
/// commit that has started (<c>CommitGateInterceptor</c>), so what committed is answered.
/// </para>
/// <para>
/// WHY 40. Above 35, so a lone hung command still ends in its own command timeout (30 s, plus up to
/// 5 s for SQL Server to acknowledge the cancel). Long enough for EF's retries to outlast a failover
/// on the throttling codes. And short enough that the API's last answer before a commit reaches the
/// visitor before the BFF gives up on the API: 40 + 5 (the cancel's acknowledgement) + 3 (releasing
/// the idempotency claim) + 5 (that release's own acknowledgement) = 53, under the BFF's 55.
/// <c>TimeoutChainTests</c> holds that order.
/// </para>
/// </remarks>
public class RequestDeadlineOptions
{
    /// <summary>Configuration section name in appsettings.json.</summary>
    public const string SectionName = "RequestDeadline";

    /// <summary>
    /// Seconds a request may run before the API cancels it and answers 503. 40 (ADR-0058).
    /// </summary>
    [Range(1, 600, ErrorMessage = "RequestDeadline:Seconds must be between 1 and 600.")]
    public int Seconds { get; set; } = 40;
}
