using AzureBank.Shared.Constants;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Bff.Http;
using AzureBank.Bff.Models;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Utilities;

namespace AzureBank.Bff.Services.Implementations;

/// <summary>
/// Access-token renewal for the BFF (ADR-0021, 06 §4.5). Every proxied API call, and the BFF's own
/// calls that carry a token, ask this for one. It hands back the session's token while it is fresh,
/// and renews it through <c>POST /api/auth/refresh</c> with the session's grant as it runs short. The
/// grant does not rotate (06 §4.3): a renewal writes nothing at the API, so a lost answer or a
/// database outage leaves nothing broken, and the same grant simply renews again.
/// </summary>
/// <remarks>
/// <para>
/// <b>When it renews</b>, by what is left of the token's own lifetime L = exp - iat (F6):
/// more than L/2 left, the token is used; between min(60 s, L/4) and L/2, it is used and a renewal
/// starts in the background; below that, or expired, the caller joins the renewal in flight or starts
/// one and waits for it at most 5 s. A renewal still pending then counts as a transient failure
/// (F15). On a transient failure the old token is used if it has more than 5 s left; otherwise the
/// caller answers 503 with <c>Retry-After</c>. An expired token is never forwarded: the API answers
/// it 401 <c>AUTH_TOKEN_EXPIRED</c>, which the SPA reads as a sign-out (06 §1).
/// </para>
/// <para>
/// <b>When a renewal may start</b>, in either branch: the session has not ended, none is in flight,
/// none failed in the last 15 s, and the grant outlives the held token by at least a second. Once a
/// renewal brings back an expiry no later than the one held — the API clamps access tokens to the
/// grant's expiry — the session stops renewing for good.
/// </para>
/// <para>
/// <b>Single flight lives on the session</b> (F2): <see cref="UserSession.Ended"/> and
/// <see cref="UserSession.InFlightRenewal"/>, under the session's one lock. It replaced a map of
/// semaphores that dropped a session's entry whenever it saw the session gone, so a second semaphore
/// could appear beside the first and two renewals run at once.
/// </para>
/// <para>
/// <b>Every renewal is detached</b>, foreground or background, with its own 30 s timeout, and never
/// takes the caller's cancellation: a browser that hangs up does not waste a renewal the API already
/// answered, and a caller's 5 s wait is only a wait (MSAL's <c>refresh_in</c> pattern).
/// </para>
/// <para>
/// <b>Only a 401 whose errorCode is <c>REFRESH_TOKEN_INVALID</c> ends the session.</b> A refused
/// service key (a key rotation applied on one side, 06 §4.7), any other 401, a 5xx, a timeout, a
/// network error or an unreadable body keeps it: none of them says the grant is dead. Before PR-1
/// any 401 ended it (measured in O0, item 5).
/// </para>
/// </remarks>
public class TokenRefresher : ITokenRefresher
{
    /// <summary>The foreground threshold is min(this, L/4).</summary>
    private static readonly TimeSpan ForegroundCeiling = TimeSpan.FromSeconds(60);

    /// <summary>The longest a caller waits for a renewal before counting it as failed (F15).</summary>
    private static readonly TimeSpan ForegroundWait = TimeSpan.FromSeconds(5);

    /// <summary>After a transient failure, a token with more than this left is still sent.</summary>
    private static readonly TimeSpan UsableMargin = TimeSpan.FromSeconds(5);

    /// <summary>After a transient failure, no renewal starts for this long.</summary>
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Each renewal's own timeout, independent of every caller. <see cref="GrantRevoker"/> waits this
    /// long for a renewal caught in flight before it revokes the grant (06 §4.6, F9c).
    /// </summary>
    internal static readonly TimeSpan RenewalTimeout = TimeSpan.FromSeconds(30);

    // Match the controller's deserialization of the API envelope (camelCase, enums-as-strings).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISessionService _sessionService;
    private readonly ILogger<TokenRefresher> _logger;

    public TokenRefresher(
        IHttpClientFactory httpClientFactory,
        ISessionService sessionService,
        ILogger<TokenRefresher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _sessionService = sessionService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AccessTokenResult> GetAccessTokenAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session is null)
        {
            return AccessTokenResult.SessionEnded;
        }

        Task? renewal;
        lock (session.SyncRoot)
        {
            if (session.Ended)
            {
                return AccessTokenResult.SessionEnded;
            }

            var now = DateTime.UtcNow;
            var left = session.TokenExpiry - now;
            var lifetime = session.TokenExpiry - session.TokenIssuedAt;

            // More than L/2 left: use the token. (Never an expired one, whatever L came out as.)
            if (left > lifetime / 2 && left > TimeSpan.Zero)
            {
                return AccessTokenResult.Token(session.AccessToken);
            }

            // Between min(60 s, L/4) and L/2: use it, and renew in the background.
            renewal = JoinOrStartRenewal(session, now);
            var foregroundAt = lifetime / 4 < ForegroundCeiling ? lifetime / 4 : ForegroundCeiling;
            if (left > foregroundAt && left > TimeSpan.Zero)
            {
                return AccessTokenResult.Token(session.AccessToken);
            }
        }

        // min(60 s, L/4) or less, or expired: wait for the renewal, at most 5 s.
        if (renewal is not null)
        {
            try
            {
                await renewal.WaitAsync(ForegroundWait, cancellationToken);
            }
            catch (TimeoutException)
            {
                // Still pending at 5 s: a transient failure (F15). It carries on, detached, and its
                // result is stored when it comes; the next request picks it up.
            }
        }

        lock (session.SyncRoot)
        {
            if (session.Ended)
            {
                return AccessTokenResult.SessionEnded;
            }

            var now = DateTime.UtcNow;
            var left = session.TokenExpiry - now;

            // A renewed token, or an old one with more than 5 s to live.
            if (left > UsableMargin)
            {
                return AccessTokenResult.Token(session.AccessToken);
            }

            // Nothing can ever renew this session: its token is what it has, until it expires.
            if (!session.CanStillRenew)
            {
                return left > TimeSpan.Zero
                    ? AccessTokenResult.Token(session.AccessToken)
                    : AccessTokenResult.SessionEnded;
            }

            // A renewal failed or is still pending, and the token has 5 s or less: the service is
            // failing, not the session. Retry after the cooldown, or shortly if a renewal is running.
            var retryAfter = session.RenewalRetryAfter is { } until && until > now
                ? until - now
                : ForegroundWait;
            return AccessTokenResult.Unavailable(Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)));
        }
    }

    /// <summary>
    /// Under the session's lock: the renewal in flight, or a new one if every condition holds, or
    /// null. A new renewal is registered on the session before the lock is released, so no second
    /// one can start beside it.
    /// </summary>
    private Task? JoinOrStartRenewal(UserSession session, DateTime now)
    {
        if (session.InFlightRenewal is { } inFlight)
        {
            return inFlight;
        }

        if (!session.CanStillRenew
            || (session.RenewalRetryAfter is { } until && now < until))
        {
            return null;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.InFlightRenewal = done.Task;
        _ = Task.Run(() => RenewAsync(session, done), CancellationToken.None);
        return done.Task;
    }

    private enum RenewalResult
    {
        Renewed,
        Transient,
        GrantInvalid,
    }

    /// <summary>
    /// One renewal, detached from every caller. Completes <paramref name="done"/> only once its
    /// result is applied, so whoever waits on it — a caller, or the revoke of an ended session —
    /// sees the session as the renewal left it.
    /// </summary>
    private async Task RenewAsync(UserSession session, TaskCompletionSource done)
    {
        var sessionId = session.SessionId;
        var endSession = false;
        try
        {
            (RenewalResult Result, RefreshResponse? Renewed) call;
            try
            {
                call = await CallRefreshAsync(sessionId, session.RefreshToken!);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Renewal failed unexpectedly for session {SessionId}", SecretPrefix.Of(sessionId));
                call = (RenewalResult.Transient, null);
            }

            lock (session.SyncRoot)
            {
                if (ReferenceEquals(session.InFlightRenewal, done.Task))
                {
                    session.InFlightRenewal = null;
                }

                if (session.Ended)
                {
                    // "Esci" or an expiry came first: the result is dropped (06 §4.5).
                    _logger.LogDebug("Renewal result dropped: session {SessionId} has ended", SecretPrefix.Of(sessionId));
                }
                else
                {
                    switch (call.Result)
                    {
                        case RenewalResult.Renewed:
                            session.RenewalRetryAfter = null;
                            if (!_sessionService.RefreshSession(session, call.Renewed!.AccessToken, call.Renewed.ExpiresAt))
                            {
                                // Nothing to gain any more: the API clamps the token to the grant's expiry.
                                session.RenewalStopped = true;
                                _logger.LogInformation(
                                    "Renewal for session {SessionId} gained nothing; the session stops renewing",
                                    SecretPrefix.Of(sessionId));
                            }
                            break;

                        case RenewalResult.Transient:
                            session.RenewalRetryAfter = DateTime.UtcNow + FailureCooldown;
                            break;

                        case RenewalResult.GrantInvalid:
                            session.RenewalStopped = true;
                            endSession = true;
                            break;
                    }
                }
            }

            if (endSession)
            {
                // Outside the lock: ending takes it again. The grant is dead at the API already, and
                // revoking it once more costs one idempotent call.
                _sessionService.EndSession(sessionId);
                _logger.LogWarning(
                    "SecurityEvent {SecurityEvent}: refresh rejected for session {SessionId}; session ended",
                    SecurityEvents.RefreshRejected, SecretPrefix.Of(sessionId));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nobody awaits this task for its exception: say so here, or it is lost.
            _logger.LogError(ex, "Renewal result could not be applied for session {SessionId}", SecretPrefix.Of(sessionId));
        }
        finally
        {
            done.TrySetResult();
        }
    }

    /// <summary>One <c>POST /api/auth/refresh</c> with the renewal's own 30 s timeout.</summary>
    private async Task<(RenewalResult Result, RefreshResponse? Renewed)> CallRefreshAsync(
        string sessionId, string grant)
    {
        var client = _httpClientFactory.CreateClient("BackendApi");
        using var timeout = new CancellationTokenSource(RenewalTimeout);

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(
                "/api/auth/refresh", new RefreshRequest { RefreshToken = grant }, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Network, or the 30 s elapsed (TaskCanceledException derives from
            // OperationCanceledException): transient, the session stays.
            _logger.LogWarning(ex,
                "Refresh call to API failed transiently for session {SessionId}", SecretPrefix.Of(sessionId));
            return (RenewalResult.Transient, null);
        }

        using (response)
        {
            string content;
            try
            {
                content = await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Failed to read the refresh response for session {SessionId}", SecretPrefix.Of(sessionId));
                return (RenewalResult.Transient, null);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var errorCode = ErrorCodeOf(content);
                if (errorCode == ErrorCodes.RefreshTokenInvalid)
                {
                    // The grant is dead (unknown, revoked or expired): the session cannot recover.
                    return (RenewalResult.GrantInvalid, null);
                }

                // A refused service key, or any 401 that does not name the grant: not the session's
                // fault, and not a reason to sign anybody out (06 §4.5, §4.7).
                _logger.LogWarning(
                    "Refresh call to API was refused with {ErrorCode} for session {SessionId}; the session is kept",
                    LogSanitizer.Sanitize(errorCode ?? "none"), SecretPrefix.Of(sessionId));
                return (RenewalResult.Transient, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Refresh call to API returned {StatusCode} for session {SessionId}",
                    (int)response.StatusCode, SecretPrefix.Of(sessionId));
                return (RenewalResult.Transient, null);
            }

            RefreshResponse? renewed;
            try
            {
                renewed = JsonSerializer.Deserialize<ApiResponse<RefreshResponse>>(content, JsonOptions)?.Data;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Failed to parse the refresh response for session {SessionId}", SecretPrefix.Of(sessionId));
                return (RenewalResult.Transient, null);
            }

            if (renewed is null || string.IsNullOrEmpty(renewed.AccessToken))
            {
                _logger.LogError(
                    "Refresh response from API was malformed for session {SessionId}", SecretPrefix.Of(sessionId));
                return (RenewalResult.Transient, null);
            }

            _logger.LogDebug("Re-minted access token for session {SessionId}", SecretPrefix.Of(sessionId));
            return (RenewalResult.Renewed, renewed);
        }
    }

    /// <summary>The <c>errorCode</c> of a problem+json body, or null when there is none to read.</summary>
    private static string? ErrorCodeOf(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("errorCode", out var code)
                && code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
