using System.Net;
using AzureBank.Shared.Options;

namespace AzureBank.Bff.Http;

/// <summary>
/// Tells the API's refusal of this host's service key from every other 401 (ADR-0057 §4.7, F4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it must not reach the browser as a 401.</b> The SPA reads any 401 on a proxied call as an
/// ended session and signs the user out. A key the API does not hold is a half-applied key rotation —
/// the service failing, not the session — and before PR-1 it signed out every user on every proxied
/// read, and through <c>ForwardUpstreamError</c> on verify-pin, set-pin and rename. Measured in O0:
/// the API's own <c>SERVICE_CREDENTIAL_REQUIRED</c> body reached the browser with its 401 intact.
/// Every road that meets it answers <see cref="ServiceUnavailable"/> instead, and the renewal and the
/// revoker treat it as transient.
/// </para>
/// <para>
/// <b>Why a header and not the body's errorCode.</b> The API marks the refusal with
/// <see cref="ServiceCredentialOptions.RefusalHeaderName"/>, which the proxy can read without
/// buffering a body it is streaming, and which no other 401 carries.
/// </para>
/// </remarks>
public static class ServiceKeyRefusal
{
    /// <summary>True when <paramref name="response"/> is the API refusing this host's service key.</summary>
    public static bool Is(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Unauthorized
        && response.Headers.TryGetValues(ServiceCredentialOptions.RefusalHeaderName, out var values)
        && values.Contains(ServiceCredentialOptions.ServiceCredentialRefusal, StringComparer.OrdinalIgnoreCase);
}
