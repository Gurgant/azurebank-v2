using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using AzureBank.Bff.Http;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Utilities;

namespace AzureBank.Bff.Services;

/// <summary>
/// One ended session's grant, waiting to be revoked at the API.
/// </summary>
/// <remarks>
/// A class and not a record on purpose: a record's generated <c>ToString</c> prints every member,
/// and one of these is a live grant. Nothing may put a grant in a log line (06 O2h).
/// </remarks>
public sealed class GrantRevocation(string sessionId, string grant, DateTime retryUntil, Task? inFlightRenewal)
{
    /// <summary>The session the grant belonged to; logged only through <c>SecretPrefix.Of</c>.</summary>
    public string SessionId { get; } = sessionId;

    /// <summary>The grant itself. Sent to the API and nowhere else.</summary>
    public string Grant { get; } = grant;

    /// <summary>When the grant expires on its own: retrying past it would revoke nothing.</summary>
    public DateTime RetryUntil { get; } = retryUntil;

    /// <summary>The renewal the session had in flight when it ended, or null (06 §4.6, F2).</summary>
    public Task? InFlightRenewal { get; } = inFlightRenewal;
}

/// <summary>
/// Revokes the grants of ended sessions at the API, one session at a time, through
/// <c>POST /api/auth/revoke</c> (06 §4.6, F12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a queue and not a call inside "Esci".</b> Signing out must answer at once and must never
/// fail because the API is down: the session is already gone from this process, and the browser has
/// nothing left to present. What remains is a grant with no holder, and revoking it is this
/// worker's job — waiting for a database outage to end if it has to. Before PR-1 "Esci" called the
/// API's logout inline, which revoked EVERY grant the user held on every device, and the user's
/// other sessions were then read as token theft at their next renewal (06 §1, measured in O0).
/// </para>
/// <para>
/// <b>It waits for the renewal the session had in flight</b> (30 s at most, the renewal's own
/// timeout). A revoke that overtook a renewal sent before it would be stamped first at the API, and
/// that renewal would arrive after the revoke: the tripwire (06 §4.3). Waiting closes that window
/// for everything but a renewal the API itself sits on for more than 30 s (06 F9c).
/// </para>
/// <para>
/// <b>Bounded</b>: a channel of <see cref="Capacity"/> items, worked by <see cref="Parallelism"/>
/// workers. Each call has a 5 s timeout, and a 5xx, a timeout, a network error or a refused service
/// key (a key rotation applied on one side, 06 §4.7) is retried with backoff until the grant expires
/// on its own. Any other refusal is final. Both ends are logged under a name an operator can search
/// for: <c>GrantRevokeAbandoned</c>, and <c>GrantRevokeDropped</c> when the channel is full. A grant
/// in either state works until its own expiry, <c>Jwt:RefreshTokenLifetimeMinutes</c> after its
/// sign-in (60 by default, 15 to 1440 allowed, 06 §4.1) — but only from inside the replica, with
/// the service key, over loopback (06 §3).
/// </para>
/// <para>
/// <b>Graceful stop (F7).</b> The replica scales to zero minutes after its last request, long before
/// the idle sweep would have ended its sessions, so the drain revokes their grants on the way out.
/// It starts the moment the host BEGINS to stop, before any service is asked to: the API is a
/// sidecar that gets its own stop signal at the same moment, and a drain sent once this process's
/// services had stopped reached an API already gone (the pre-review of PR-1). Every session still
/// held is ended then, and their grants go to the API at once, in one call — those whose session had
/// a renewal in flight in a second call, once it settles, 5 s at most. When the workers have
/// stopped, what is left goes in one more call: a session a request still in flight stored or ended
/// after the first half, and what was queued or being retried. The log says how many were revoked
/// and how many were left. Each half runs on a budget of its own, never on the host's shutdown
/// token, which is already cancelled when a request held Kestrel's stop past its timeout. A grant
/// the API did not get — a kill, or an API that stopped first, as it does under compose — is live
/// until its cap, with nobody holding it.
/// </para>
/// </remarks>
public sealed class GrantRevoker : BackgroundService
{
    /// <summary>The most revocations waiting at once; beyond it they are dropped and logged.</summary>
    /// <remarks>
    /// A choice (06 §4.6 leaves it open), far above the sessions one replica holds. Its own number on
    /// purpose, not <c>RevokeRequest.MaxRefreshTokens</c>: that one is the API's limit per call, and
    /// the drain already splits a larger batch into calls of that size, so neither needs the other.
    /// </remarks>
    internal const int Capacity = 1000;

    /// <summary>How many revocations run at once (06 §4.6, a choice, not a measurement).</summary>
    internal const int Parallelism = 4;

    /// <summary>Longest wait for a captured renewal: the renewal's own timeout.</summary>
    private static readonly TimeSpan RenewalWait = Implementations.TokenRefresher.RenewalTimeout;

    /// <summary>Each call to <c>/revoke</c>, independent of any caller.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// On a graceful stop, how long the drain waits for renewals caught in flight. Shorter than
    /// <see cref="RenewalWait"/>: the whole drain has to fit the host's shutdown grace period.
    /// </summary>
    private static readonly TimeSpan DrainRenewalWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Each half of the graceful-stop drain, from its start to its last call: the renewal wait and
    /// then a call of <see cref="CallTimeout"/>, with room for a second call past
    /// <c>RevokeRequest.MaxRefreshTokens</c> grants. Its own, not the host's shutdown token: a request
    /// that holds Kestrel's graceful stop to its timeout cancels that token before the drain has
    /// sent anything.
    /// </summary>
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(15);

    private readonly Channel<GrantRevocation> _queue = Channel.CreateBounded<GrantRevocation>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait });

    /// <summary>What a worker has taken and not finished: the drain sends these too.</summary>
    private readonly ConcurrentDictionary<GrantRevocation, byte> _inProgress =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>The grants of the sessions ended because the host is stopping.</summary>
    private readonly ConcurrentQueue<GrantRevocation> _endedAtStop = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _services;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<GrantRevoker> _logger;
    private CancellationTokenRegistration _onStopping;

    /// <summary>The graceful-stop drain, once it has started: every later stop waits for it.</summary>
    private TaskCompletionSource? _drain;

    /// <summary>
    /// The first half of the drain, started when the host began to stop; null until then. Set under
    /// <see cref="_drainGate"/>, so a stop that reads it finds either nothing or the whole first half.
    /// </summary>
    private Task? _drainAtStopping;
    private readonly Lock _drainGate = new();

    /// <summary>How many grants the drain has taken, and how many of them the API revoked.</summary>
    private int _drainTaken;
    private int _drainRevoked;

    public GrantRevoker(
        IHttpClientFactory httpClientFactory,
        IServiceProvider services,
        IHostApplicationLifetime lifetime,
        ILogger<GrantRevoker> logger)
    {
        _httpClientFactory = httpClientFactory;
        // The store is resolved when the host stops, not injected: the store queues its ended
        // sessions here, so injecting it would be a cycle. SessionCleanupService reads it the same way.
        _services = services;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>
    /// Queues an ended session's grant. Never blocks and never throws: when the channel is full the
    /// grant is dropped and <c>GrantRevokeDropped</c> is logged.
    /// </summary>
    public void Queue(GrantRevocation revocation)
    {
        if (_queue.Writer.TryWrite(revocation))
        {
            return;
        }

        _logger.LogWarning(
            "GrantRevokeDropped: the revoke queue is full or closed; the grant of session {SessionId} "
            + "stays live until {ExpiresAt}",
            SecretPrefix.Of(revocation.SessionId), revocation.RetryUntil);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // ApplicationStopping fires before any hosted service is asked to stop, so from here on
        // nothing renews a session and no renewal can race the drain (06 F7).
        _onStopping = _lifetime.ApplicationStopping.Register(BeginDrain);
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Parallelism)
            .Select(_ => Task.Run(() => WorkAsync(stoppingToken), CancellationToken.None)));

    /// <summary>
    /// The host has begun to stop, and nothing has been asked to stop yet — on Azure the API sidecar
    /// has just been sent its own stop signal. Ends every session held and starts revoking their
    /// grants now rather than after this process's services have stopped (06 F7).
    /// </summary>
    private void BeginDrain()
    {
        lock (_drainGate)
        {
            if (_drainAtStopping is not null)
            {
                return;
            }

            EndEverySession();
            var ended = TakeEndedAtStop();

            // Task.Run: this runs on the thread that is stopping the host, which must not wait for
            // the API.
            _drainAtStopping = Task.Run(() => DrainAsync(ended));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // The workers first. Each leaves the item it was on in _inProgress when it stops.
        await base.StopAsync(cancellationToken);

        /*
          ONE DRAIN, AND EVERY STOP WAITS FOR IT. Measured under WebApplicationFactory: this was
          called twice per stop, and the first version drained twice, sending the same grants again.
          The next one returned at once from the second call, and its drain then failed with
          ObjectDisposedException in the HTTP client factory: the host's services were disposed
          under it. Read, not traced: Program's own app.Run() stops the host and then disposes it,
          while the factory's dispose stops it as well. Waiting here holds either caller until the
          one drain is done.
        */
        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _drain, mine, null) is { } running)
        {
            await running.Task;
            return;
        }

        try
        {
            // The first half, started when the host began to stop, on its own budget. None when
            // nothing signalled the stop first: the half below then takes every grant.
            Task? first;
            lock (_drainGate)
            {
                first = _drainAtStopping;
            }
            if (first is not null)
            {
                await first;
            }

            // The second half: only what the first could not have taken. Ending every session again
            // finds those a request still in flight stored after the first pass (a session already
            // ended is not ended twice); then what the workers had queued or were retrying.
            EndEverySession();
            var rest = TakeEndedAtStop();
            rest.AddRange(_inProgress.Keys);
            while (_queue.Reader.TryRead(out var queued))
            {
                rest.Add(queued);
            }

            // From here a late ending is logged as dropped rather than queued for nobody.
            _queue.Writer.TryComplete();

            await DrainAsync(rest);

            // Said even when there was nothing to send, so "the drain ran and found no grant" reads
            // differently from "the drain never ran" (06 F7, §11 rely on this count).
            var revoked = Volatile.Read(ref _drainRevoked);
            var left = Volatile.Read(ref _drainTaken) - revoked;
            if (left == 0)
            {
                _logger.LogInformation(
                    "Graceful stop: {Revoked} grants revoked, {Left} left", revoked, left);
            }
            else
            {
                _logger.LogWarning(
                    "Graceful stop: {Revoked} grants revoked, {Left} left live until they expire", revoked, left);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A stop must never fail because a revoke did: the grants left die at their cap.
            _logger.LogError(ex, "Graceful stop: the drain of the held grants failed");
        }
        finally
        {
            mine.TrySetResult();
        }
    }

    /// <summary>
    /// Revokes <paramref name="grants"/> on a graceful stop, on a budget of its own: those whose
    /// session had no renewal in flight in one call at once, the others in one call once their
    /// renewals settle, <see cref="DrainRenewalWait"/> at most (06 §4.6, F7). Not retried: the host
    /// is going away, and a grant this misses dies at its cap with nobody holding it.
    /// </summary>
    private async Task DrainAsync(IReadOnlyList<GrantRevocation> grants)
    {
        if (grants.Count == 0)
        {
            return;
        }

        Interlocked.Add(ref _drainTaken, grants.Count);
        using var budget = new CancellationTokenSource(DrainBudget);

        var now = new List<GrantRevocation>();
        var afterRenewal = new List<(GrantRevocation Grant, Task Renewal)>();
        foreach (var grant in grants)
        {
            if (grant.InFlightRenewal is { IsCompleted: false } renewal)
            {
                afterRenewal.Add((grant, renewal));
            }
            else
            {
                now.Add(grant);
            }
        }

        await Task.WhenAll(
            SendAtStopAsync(now, budget.Token),
            SendAfterRenewalsAsync(afterRenewal, budget.Token));
    }

    /// <summary>
    /// Waits for the renewals the sessions had in flight, <see cref="DrainRenewalWait"/> at most, then
    /// sends their grants: a revoke that overtook a renewal sent before it would make that renewal
    /// the tripwire at the API (06 §4.6, F2).
    /// </summary>
    private async Task SendAfterRenewalsAsync(
        List<(GrantRevocation Grant, Task Renewal)> pending, CancellationToken budget)
    {
        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending.Select(p => p.Renewal)).WaitAsync(DrainRenewalWait, budget);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Out of time: revoke anyway. A late renewal costs one audit row, never a sign-out.
        }

        await SendAtStopAsync([.. pending.Select(p => p.Grant)], budget);
    }

    /// <summary>
    /// One call (more only past <c>RevokeRequest.MaxRefreshTokens</c> grants), counting what the
    /// API revoked. Never throws: what was not sent is counted as left.
    /// </summary>
    private async Task SendAtStopAsync(IReadOnlyList<GrantRevocation> grants, CancellationToken budget)
    {
        try
        {
            foreach (var chunk in grants.Chunk(RevokeRequest.MaxRefreshTokens))
            {
                var (result, _) = await SendAsync([.. chunk.Select(p => p.Grant)], budget);
                if (result == SendResult.Revoked)
                {
                    Interlocked.Add(ref _drainRevoked, chunk.Length);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The drain's budget ran out.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A stop must never fail because a revoke did: the grants left die at their cap.
            _logger.LogError(ex, "Graceful stop: a revoke call of the drain failed");
        }
    }

    /// <summary>The grants of the sessions ended because the host is stopping, not yet taken.</summary>
    private List<GrantRevocation> TakeEndedAtStop()
    {
        var ended = new List<GrantRevocation>();
        while (_endedAtStop.TryDequeue(out var grant))
        {
            ended.Add(grant);
        }

        return ended;
    }

    public override void Dispose()
    {
        _onStopping.Dispose();
        base.Dispose();
    }

    private void EndEverySession()
    {
        try
        {
            var store = _services.GetRequiredService<ITokenStoreService>();
            foreach (var grant in store.EndAllSessions())
            {
                _endedAtStop.Enqueue(grant);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Could not end the sessions held when the host began to stop");
        }
    }

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var revocation in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                _inProgress[revocation] = 0;
                try
                {
                    await RevokeAsync(revocation, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Stopping: left in _inProgress for the drain.
                    throw;
                }
                catch (Exception ex)
                {
                    // An unhandled exception would stop the host (BackgroundService's default), so
                    // one bad item costs its own grant and nothing else.
                    _logger.LogError(ex,
                        "GrantRevokeAbandoned: revoking the grant of session {SessionId} failed unexpectedly",
                        SecretPrefix.Of(revocation.SessionId));
                }

                _inProgress.TryRemove(revocation, out _);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The drain in StopAsync takes what is left.
        }
    }

    private async Task RevokeAsync(GrantRevocation revocation, CancellationToken stoppingToken)
    {
        if (revocation.InFlightRenewal is { IsCompleted: false } renewal)
        {
            try
            {
                await renewal.WaitAsync(RenewalWait, stoppingToken);
            }
            catch (TimeoutException)
            {
                // The renewal's own timeout has passed as well: send the revoke.
            }
        }

        var delay = FirstRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            var (result, status) = await SendAsync([revocation.Grant], stoppingToken);
            switch (result)
            {
                case SendResult.Revoked:
                    _logger.LogDebug("Grant of session {SessionId} revoked", SecretPrefix.Of(revocation.SessionId));
                    return;

                case SendResult.Refused:
                    _logger.LogWarning(
                        "GrantRevokeAbandoned: the API answered {StatusCode} to the revoke of session "
                        + "{SessionId}'s grant, which stays live until {ExpiresAt}",
                        status, SecretPrefix.Of(revocation.SessionId), revocation.RetryUntil);
                    return;
            }

            if (DateTime.UtcNow + delay >= revocation.RetryUntil)
            {
                _logger.LogWarning(
                    "GrantRevokeAbandoned: gave up revoking the grant of session {SessionId} after "
                    + "{Attempts} attempts; it expires at {ExpiresAt}",
                    SecretPrefix.Of(revocation.SessionId), attempt, revocation.RetryUntil);
                return;
            }

            await Task.Delay(delay, stoppingToken);
            delay = delay * 2 < MaxRetryDelay ? delay * 2 : MaxRetryDelay;
        }
    }

    private enum SendResult
    {
        Revoked,
        Transient,
        Refused,
    }

    /// <summary>
    /// One call to <c>/revoke</c> with its own 5 s timeout. Throws only when
    /// <paramref name="abortToken"/> is cancelled.
    /// </summary>
    private async Task<(SendResult Result, int? Status)> SendAsync(
        IReadOnlyList<string> grants, CancellationToken abortToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(abortToken);
        timeout.CancelAfter(CallTimeout);

        try
        {
            var client = _httpClientFactory.CreateClient("BackendApi");
            using var response = await client.PostAsJsonAsync(
                "/api/auth/revoke", new RevokeRequest { RefreshTokens = grants }, timeout.Token);

            if (response.IsSuccessStatusCode)
            {
                return (SendResult.Revoked, (int)response.StatusCode);
            }

            // A 5xx says "later" (the API answers 503 when its database write fails, RFC 7009
            // §2.2.1), and so does a refused key: that is a rotation half applied, not a verdict.
            var transient = response.StatusCode >= HttpStatusCode.InternalServerError
                || ServiceKeyRefusal.Is(response);
            return (transient ? SendResult.Transient : SendResult.Refused, (int)response.StatusCode);
        }
        catch (HttpRequestException)
        {
            return (SendResult.Transient, null);
        }
        catch (OperationCanceledException) when (!abortToken.IsCancellationRequested)
        {
            // Our own 5 s timeout, not a stop.
            return (SendResult.Transient, null);
        }
    }
}
