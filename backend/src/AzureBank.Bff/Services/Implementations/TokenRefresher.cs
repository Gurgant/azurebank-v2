using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Bff.Models;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;

namespace AzureBank.Bff.Services.Implementations;

/// <summary>
/// On-demand, inline access-token re-mint for the BFF (ADR-0021, PR-2 / OAuth Browser-Based-Apps
/// BCP §6.1.2.2). When a proxied API call (or a verify-pin / set-pin / logout call) is about to
/// carry an access token that is within the skew window, this exchanges the session's refresh
/// token for a fresh access + refresh pair via <c>POST /api/auth/refresh</c> and stores the
/// rotated pair back on the session — so the 15-minute JWT no longer hard-kills the session.
///
/// Single-flight: one refresh in flight per session (a per-session <see cref="SemaphoreSlim"/>),
/// with a double-check after acquiring so racing callers share the winner's result rather than
/// each rotating (which would trip the API's reuse-detection).
/// </summary>
public class TokenRefresher : ITokenRefresher
{
    // Re-mint proactively when the access token has at most this long left, so we never ship a
    // token that dies mid-upstream-call. Access tokens live 15 min, so this is ~4 refreshes/hour
    // per active session at most.
    private static readonly TimeSpan TokenRefreshSkew = TimeSpan.FromSeconds(60);

    // Match the controller's deserialization of the API envelope (camelCase, enums-as-strings).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // Per-session single-flight gates. Entries are never DISPOSED (a disposed semaphore would
    // fault a concurrent waiter) — only REMOVED. A gate is dropped the moment its session is
    // observed gone: a 401 revoke, or any call bearing a now-dead session id (the common
    // "stale cookie returns after the session expired" path). That bounds the map to live +
    // recently-touched sessions. A session abandoned WITHOUT the cookie ever being re-sent leaks
    // one small semaphore until process restart — bounded, and moot once a distributed session
    // store replaces this (the Redis residual in ADR-0021).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

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
    public async Task<string?> GetFreshAccessTokenAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session is null)
        {
            // The session is gone (expired / logged out): drop any lingering single-flight gate.
            _gates.TryRemove(sessionId, out _);
            return null;
        }

        // Fast path: comfortably fresh — no lock, no refresh.
        if (!NeedsRefresh(session))
        {
            return session.AccessToken;
        }

        // A tokenless session (register's best-effort issuance failed) cannot re-mint: serve the
        // current token until it truly expires, then nothing (the store reaps it).
        if (session.RefreshToken is null)
        {
            return session.IsTokenExpired ? null : session.AccessToken;
        }

        var gate = _gates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Double-check: a concurrent caller may have refreshed (or the session may have gone)
            // while we waited on the gate.
            session = _sessionService.GetSession(sessionId);
            if (session is null)
            {
                _gates.TryRemove(sessionId, out _);
                return null;
            }
            if (!NeedsRefresh(session))
            {
                return session.AccessToken;
            }
            if (session.RefreshToken is null)
            {
                return session.IsTokenExpired ? null : session.AccessToken;
            }

            return await RefreshAsync(sessionId, session.RefreshToken, session.AccessToken, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool NeedsRefresh(UserSession session) =>
        session.TokenExpiry - DateTime.UtcNow <= TokenRefreshSkew;

    private async Task<string?> RefreshAsync(
        string sessionId, string refreshToken, string currentAccessToken, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("BackendApi");

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(
                "/api/auth/refresh", new RefreshRequest { RefreshToken = refreshToken }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Transient (network / timeout / caller aborted before a response): do NOT revoke —
            // the session may still be usable and the upstream call fails naturally / retries.
            // (TaskCanceledException derives from OperationCanceledException, so both are caught.)
            _logger.LogWarning(ex,
                "Refresh call to API failed transiently for session {SessionId}", Redact(sessionId));
            return currentAccessToken;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The refresh token is dead (revoked / expired / reuse-detected) — the session
                // cannot recover, so end it and force a fresh login.
                _sessionService.RevokeSession(sessionId);
                _gates.TryRemove(sessionId, out _);
                _logger.LogWarning(
                    "SecurityEvent {SecurityEvent}: refresh rejected for session {SessionId}; session revoked",
                    "RefreshRejected", Redact(sessionId));
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // A non-401 error (5xx) is transient-ish: keep the session, serve the current token.
                _logger.LogWarning(
                    "Refresh call to API returned {StatusCode} for session {SessionId}",
                    (int)response.StatusCode, Redact(sessionId));
                return currentAccessToken;
            }

            // We have a 200: the API has ALREADY rotated the refresh token. Read and store the
            // successor even if the CALLER's request was aborted (read with CancellationToken.None,
            // not the caller's token) — discarding a committed rotation would leave the old, now
            // server-side-revoked token on the session and force a needless re-login on next use.
            RefreshResponse? refreshed;
            try
            {
                var content = await response.Content.ReadAsStringAsync(CancellationToken.None);
                refreshed = JsonSerializer.Deserialize<ApiResponse<RefreshResponse>>(content, JsonOptions)?.Data;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException)
            {
                _logger.LogWarning(ex,
                    "Failed to read/parse the refresh response for session {SessionId}", Redact(sessionId));
                return currentAccessToken;
            }

            if (refreshed is null || string.IsNullOrEmpty(refreshed.AccessToken))
            {
                _logger.LogError(
                    "Refresh response from API was malformed for session {SessionId}", Redact(sessionId));
                return currentAccessToken;
            }

            _sessionService.RefreshSession(
                sessionId, refreshed.AccessToken, refreshed.ExpiresAt, refreshed.RefreshToken);
            _logger.LogDebug("Re-minted access token for session {SessionId}", Redact(sessionId));
            return refreshed.AccessToken;
        }
    }

    private static string Redact(string sessionId) => sessionId[..Math.Min(8, sessionId.Length)];
}
