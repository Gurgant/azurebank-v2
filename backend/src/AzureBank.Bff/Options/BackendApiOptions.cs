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
    /// How long any one call on the BFF's own client may take, in seconds, before it is abandoned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 100, which is <c>HttpClient</c>'s own default: naming it changed nothing (ADR-0057 §8). It
    /// exists so PR-2 can set the BFF's wait above the API's request deadline without a code
    /// change, and so the value is written where an operator looks rather than implied by a
    /// framework default.
    /// </para>
    /// <para>
    /// Three calls on this client are bounded more tightly on purpose, and this does not lengthen
    /// them: the renewal (30 s, detached), <c>/api/auth/revoke</c> (5 s, retried) and <c>/me</c>'s
    /// read-through (5 s, then the cached copy). None writes anything a late answer could lose
    /// (ADR-0057 §4.5-4.6).
    /// </para>
    /// <para>
    /// At most <see cref="MaxTimeoutSeconds"/>, checked at startup: <c>HttpClient.Timeout</c> refuses
    /// anything longer, and it would refuse it on every <c>CreateClient</c>, long after a startup
    /// check that let it through.
    /// </para>
    /// </remarks>
    public int TimeoutSeconds { get; set; } = 100;

    /// <summary>
    /// The longest <see cref="TimeoutSeconds"/> <c>HttpClient</c> takes: <c>int.MaxValue</c>
    /// milliseconds, about 24.8 days, in whole seconds.
    /// </summary>
    public const int MaxTimeoutSeconds = int.MaxValue / 1000;
}
