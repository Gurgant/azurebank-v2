namespace AzureBank.Bff.Options;

/// <summary>
/// The BFF's own client to the API (the named <c>BackendApi</c> client). Bound from the
/// "BackendApi" section. <c>BaseUrl</c> lives in the same section and is read where the client is
/// configured, live, for the reason <c>ServiceCredentialHandler</c> gives.
/// </summary>
public sealed class BackendApiOptions
{
    public const string SectionName = "BackendApi";

    /// <summary>
    /// How long the BFF waits on the API, in seconds, before it gives up and answers the outage 503
    /// itself: any one call on its own client, and any proxied call (ADR-0058).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 55, above the API's own 40-second request deadline plus up to 5 s for SQL Server to
    /// acknowledge the cancelled command, the 3-second release of an idempotency claim, and 5 s more
    /// if that release is cancelled too: 53 s. So every answer the API gives before a commit,
    /// <c>applied: false</c> included, reaches the visitor, and the BFF's own 503, which cannot say
    /// whether anything was applied, is left for an API that has stopped answering
    /// (TimeoutChainTests). It was 100, <c>HttpClient</c>'s default, and the proxy waited YARP's 100
    /// as well.
    /// </para>
    /// <para>
    /// One value for both roads: it is this client's <c>HttpClient.Timeout</c>, and
    /// <c>BackendTimeoutConfigFilter</c> makes it every proxy cluster's activity timeout. The BFF
    /// never sends a call again when it runs out: whether to try again is the SPA's decision, which
    /// the 503's <c>Retry-After</c> informs.
    /// </para>
    /// <para>
    /// Five calls on this client are bounded more tightly on purpose, and this does not lengthen
    /// them: the renewal (30 s, detached), <c>/api/auth/revoke</c> (5 s, retried) and <c>/me</c>'s
    /// read-through (5 s, then the cached copy) (ADR-0057 §4.5-4.6), the session-stamp poll (5 s,
    /// <c>SessionStampWatcher</c>) and the health probe (3 s, <c>BackendApiHealthCheck</c>). None
    /// writes anything a late answer could lose.
    /// </para>
    /// <para>
    /// At most <see cref="MaxTimeoutSeconds"/>, checked at startup: <c>HttpClient.Timeout</c> refuses
    /// anything longer, and it would refuse it on every <c>CreateClient</c>, long after a startup
    /// check that let it through.
    /// </para>
    /// </remarks>
    public int TimeoutSeconds { get; set; } = 55;

    /// <summary>
    /// The longest <see cref="TimeoutSeconds"/> <c>HttpClient</c> takes: <c>int.MaxValue</c>
    /// milliseconds, about 24.8 days, in whole seconds.
    /// </summary>
    public const int MaxTimeoutSeconds = int.MaxValue / 1000;
}
