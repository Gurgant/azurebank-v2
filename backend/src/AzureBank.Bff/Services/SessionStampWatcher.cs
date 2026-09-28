using System.Net.Http.Json;
using System.Text.Json;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;

namespace AzureBank.Bff.Services;

/// <summary>
/// Reads the session stamps of the users who hold sessions, every 15 seconds, through
/// <c>POST /api/auth/session-stamps</c>, into <see cref="SessionStamps"/> (ADR-0057 §5.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Signing one user out of every session (<c>POST /api/auth/logout</c>, or a runbook's
/// SQL) revokes the user's grants and raises the user's stamp. The revoke alone ends each BFF session
/// only at its next renewal, after up to half an access token's life of continued use (7.5 minutes
/// for a 15-minute token). With this, a session whose stamp is below the one read here is refused at
/// its next request, at most one period after the sign-out.
/// </para>
/// <para>
/// <b>No session, no call.</b> A tick with nobody signed in sends nothing, so an idle replica never
/// wakes the API or its database.
/// </para>
/// <para>
/// <b>A failed poll keeps the last values</b>: a refusal, a 5xx, a timeout, a network error or an
/// unreadable answer changes nothing in the map, and the next tick asks again. The first failure of a
/// run is a Warning and the rest are Debug, so an outage costs one line, not one every 15 s; the
/// recovery is logged too.
/// </para>
/// </remarks>
public sealed class SessionStampWatcher : BackgroundService
{
    /// <summary>
    /// How often the stamps are read while anyone holds a session (ADR-0057 §5.3, a choice).
    /// </summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(15);

    /// <summary>Each call, independent of the client's own timeout; shorter than a period.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _services;
    private readonly SessionStamps _stamps;
    private readonly ILogger<SessionStampWatcher> _logger;
    private readonly PeriodicTimer _timer;
    private bool _failing;

    public SessionStampWatcher(
        IHttpClientFactory httpClientFactory,
        IServiceProvider services,
        SessionStamps stamps,
        TimeProvider timeProvider,
        ILogger<SessionStampWatcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        /*
          The store is resolved at each tick, not injected. Building it reads the session options,
          and the host builds every hosted service BEFORE the options are validated at start: with
          the store injected here, a whitespace Session:CookieName failed in the store's constructor
          instead of in ValidateOnStart, and the test host's dispose then threw ArgumentNullException
          (measured 2026-09-28, SessionOptionsPipelineTests). GrantRevoker reads it the same way.
        */
        _services = services;
        _stamps = stamps;
        _logger = logger;

        /*
          The timer starts here, not in ExecuteAsync. On .NET 10 the host runs ExecuteAsync on the
          thread pool after StartAsync returns (measured 2026-09-28 on runtime 10.0.12: in 197 of
          200 starts it had not begun by then), so a timer made there could be made after a fake
          clock had already been advanced, and that period's tick never came: the watcher's tests
          failed at random under load. Made here, it exists once the host has built the service,
          and a tick that falls due before ExecuteAsync first waits is kept for that wait.
        */
        _timer = new PeriodicTimer(Period, timeProvider);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await PollAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An unhandled exception would stop the host (BackgroundService's default); one
                    // bad tick costs that tick and nothing else.
                    _logger.LogError(ex, "SessionStampPollFailed: the poll failed unexpectedly");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>Stops the timer too, which a host that never ran <see cref="ExecuteAsync"/> leaves running.</summary>
    public override void Dispose()
    {
        _timer.Dispose();
        base.Dispose();
    }

    /// <summary>One tick: read the stamps of every user who holds a session, if anyone does.</summary>
    private async Task PollAsync(CancellationToken stoppingToken)
    {
        var signedIn = _services.GetRequiredService<ITokenStoreService>().SignedInUserIds();
        _stamps.RetainOnly(signedIn);
        if (signedIn.Count == 0)
        {
            return;
        }

        foreach (var chunk in signedIn.Chunk(SessionStampsRequest.MaxUserIds))
        {
            var answer = await ReadAsync(chunk, stoppingToken);
            if (answer is null)
            {
                // The tick stops here and the map keeps what it had; the next tick is 15 s away.
                return;
            }

            // Observe only ever raises a value, so applying one call's answer before the next call
            // can lower nothing, whatever the next call does.
            foreach (var stamp in answer)
            {
                _stamps.Observe(stamp.UserId, stamp.SessionStamp);
            }
        }

        if (_failing)
        {
            _failing = false;
            _logger.LogInformation("SessionStampPoll: reading session stamps works again");
        }
    }

    /// <summary>One call; null on any failure, which has been logged.</summary>
    private async Task<IReadOnlyList<UserSessionStamp>?> ReadAsync(
        IReadOnlyList<Guid> userIds, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(CallTimeout);

        try
        {
            var client = _httpClientFactory.CreateClient("BackendApi");
            using var response = await client.PostAsJsonAsync(
                "/api/auth/session-stamps", new SessionStampsRequest { UserIds = userIds }, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Log(FailureLevel(),
                    "SessionStampPollFailed: the API answered {StatusCode}; the last stamps read are kept",
                    (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<ApiResponse<SessionStampsResponse>>(
                Json, timeout.Token);
            if (body?.Data?.Stamps is not { } stamps)
            {
                _logger.Log(FailureLevel(),
                    "SessionStampPollFailed: the answer carried no stamps; the last stamps read are kept");
                return null;
            }

            return stamps;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException
            || (ex is OperationCanceledException && !stoppingToken.IsCancellationRequested))
        {
            _logger.Log(FailureLevel(),
                "SessionStampPollFailed: the call failed ({ExceptionType}); the last stamps read are kept",
                ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Warning for the first failure of a run, Debug for the rest.</summary>
    private LogLevel FailureLevel()
    {
        var level = _failing ? LogLevel.Debug : LogLevel.Warning;
        _failing = true;
        return level;
    }
}
