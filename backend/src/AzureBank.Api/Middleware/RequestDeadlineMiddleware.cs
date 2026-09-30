using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Middleware;

/// <summary>
/// Exempts an endpoint from the request deadline (ADR-0058): it runs to completion once it has
/// started, whatever the caller does, and its commits are never gated.
/// </summary>
/// <remarks>
/// On the three token endpoints that are not sign-ins: refresh, revoke and logout (ADR-0057 §4.3).
/// Refresh only reads for an active grant, and the audit rows of its two refusals must not be taken
/// back by a caller that hangs up; revoke and logout are short and idempotent, and finishing them
/// after the caller has gone only ends a session sooner. Endpoint metadata rather than a path list,
/// so the exemption travels with the action.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class NoRequestDeadlineAttribute : Attribute;

/// <summary>
/// The request deadline of one request, as a request feature (ADR-0058): the one token the request
/// runs under, and the state the commit gate and the exception handlers read.
/// </summary>
public interface IRequestDeadline
{
    /// <summary>
    /// The token the request runs under (<c>HttpContext.RequestAborted</c> inside the middleware):
    /// cancelled when the deadline fires or the client hangs up, and never once a commit has started.
    /// </summary>
    CancellationToken Token { get; }

    /// <summary>The client's own token, which the middleware found on the request.</summary>
    CancellationToken ClientAborted { get; }

    /// <summary>True when the deadline's timer cancelled the request.</summary>
    bool FiredByDeadline { get; }

    /// <summary>True when the client hanging up cancelled the request.</summary>
    bool FiredByClient { get; }

    /// <summary>
    /// True once any commit of the request's own context has been let start. Sticky: a commit that
    /// failed may or may not have landed, so a request that started one never says "nothing was
    /// applied".
    /// </summary>
    bool CommitEntered { get; }

    /// <summary>How long the request has run under the deadline.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>
    /// Asked by the commit gate before a commit starts: false when the deadline has already cancelled
    /// the request (the commit must not start), true otherwise, and from then on nothing cancels the
    /// token until <see cref="CommitFailed"/>.
    /// </summary>
    bool TryEnterCommit();

    /// <summary>A commit that was let start failed: the deadline applies again, at its original instant.</summary>
    void CommitFailed();

    /// <summary>A commit that was let start landed: the deadline stays off for the rest of the request.</summary>
    void Committed();

    /// <summary>
    /// Turns the deadline off before a result is written, so a result that exists is sent. True when
    /// a commit has started and the token can no longer be cancelled: the response is then written
    /// under it whole, even to a client that has gone, so an idempotent answer is stored whole.
    /// </summary>
    bool Disarm();
}

/// <summary>
/// The state machine behind <see cref="IRequestDeadline"/>. One cancellation source, cancelled by a
/// timer or by the client's token, and every transition under one lock.
/// </summary>
/// <remarks>
/// <para>
/// ONE SOURCE, NOT A LINKED PAIR. The deadline and the client's hang-up cancel the same token, and
/// the lock decides which of "cancel" and "a commit starts" happens first. A linked source would
/// cancel on the client's token by itself, including in the middle of a commit.
/// </para>
/// <para>
/// THE CALLBACKS RUN ON A TIMER THREAD OR THE CLIENT'S, and they cancel with <c>CancelAsync</c>, so
/// no continuation of the request runs on either. No exception may leave one: a timer callback that
/// throws ends the process. The source can be disposed between the state check and the cancel (the
/// request finished at that instant), which is the <c>ObjectDisposedException</c> caught below.
/// </para>
/// </remarks>
public sealed class RequestDeadline : IRequestDeadline, IDisposable
{
    private enum State
    {
        Armed,
        Fired,
        Committing,
        Committed,
        Disarmed,
        Done,
    }

    private enum Cause
    {
        None,
        Deadline,
        Client,
    }

    private readonly object _sync = new();
    private readonly CancellationTokenSource _source = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _budget;
    private readonly long _started;
    private readonly ILogger _logger;
    private readonly ITimer _timer;
    private readonly CancellationTokenRegistration _clientRegistration;
    private State _state = State.Armed;
    private Cause _cause = Cause.None;
    private bool _commitEntered;

    public RequestDeadline(TimeProvider time, TimeSpan budget, CancellationToken clientAborted, ILogger logger)
    {
        _time = time;
        _budget = budget;
        _logger = logger;
        _started = time.GetTimestamp();
        ClientAborted = clientAborted;
        Token = _source.Token;

        // Without the request's ExecutionContext: the timer's callback needs none of its AsyncLocals
        // (the HttpContext among them), and must not keep them alive on the timer.
        if (ExecutionContext.IsFlowSuppressed())
        {
            _timer = CreateTimer(time, budget);
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                _timer = CreateTimer(time, budget);
            }
        }

        // Last, because a token that is already cancelled runs the callback here and now.
        _clientRegistration = clientAborted.UnsafeRegister(
            static state => ((RequestDeadline)state!).Fire(Cause.Client), this);
    }

    private ITimer CreateTimer(TimeProvider time, TimeSpan budget) =>
        time.CreateTimer(
            static state => ((RequestDeadline)state!).Fire(Cause.Deadline),
            this, budget, Timeout.InfiniteTimeSpan);

    public CancellationToken Token { get; }

    public CancellationToken ClientAborted { get; }

    public bool FiredByDeadline
    {
        get { lock (_sync) { return _cause == Cause.Deadline; } }
    }

    public bool FiredByClient
    {
        get { lock (_sync) { return _cause == Cause.Client; } }
    }

    public bool CommitEntered
    {
        get { lock (_sync) { return _commitEntered; } }
    }

    public TimeSpan Elapsed => _time.GetElapsedTime(_started);

    public bool TryEnterCommit()
    {
        lock (_sync)
        {
            if (_state == State.Fired)
            {
                return false;
            }

            if (_state == State.Armed)
            {
                _state = State.Committing;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            _commitEntered = true;
            return true;
        }
    }

    public void CommitFailed()
    {
        bool clientGone;
        lock (_sync)
        {
            if (_state != State.Committing)
            {
                return;
            }

            // At the ORIGINAL instant: a failed commit does not buy the request more time. Past it,
            // the timer fires at once, on its own thread.
            _state = State.Armed;
            var left = _budget - Elapsed;
            _timer.Change(left > TimeSpan.Zero ? left : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            clientGone = ClientAborted.IsCancellationRequested;
        }

        // A client that hung up while the commit ran was ignored then; with the commit failed, it
        // cancels the request now, as it would have.
        if (clientGone)
        {
            Fire(Cause.Client);
        }
    }

    public void Committed()
    {
        lock (_sync)
        {
            if (_state == State.Committing)
            {
                _state = State.Committed;
            }
        }
    }

    public bool Disarm()
    {
        lock (_sync)
        {
            if (_state == State.Armed)
            {
                _state = State.Disarmed;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            return _commitEntered && _state is State.Committing or State.Committed or State.Disarmed;
        }
    }

    private void Fire(Cause cause)
    {
        lock (_sync)
        {
            if (_state != State.Armed)
            {
                return;
            }

            _state = State.Fired;
            _cause = cause;
        }

        try
        {
            _ = _source.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // The request finished between the check above and this line: nothing is left to cancel.
        }
        catch (Exception e)
        {
            _logger.LogError(e, "The request deadline could not cancel its request ({Reason})", cause.ToString());
        }
    }

    /// <summary>
    /// Ends the deadline: nothing fires after this, and what it recorded stays readable for the
    /// exception handlers above the middleware.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _state = State.Done;
        }

        _timer.Dispose();
        _clientRegistration.Dispose();
        _source.Dispose();
    }
}

/// <summary>
/// The request scope's own deadline, for the commit gate: set by <see cref="RequestDeadlineMiddleware"/>
/// on the REQUEST's scope only, so a context resolved in a fresh scope (a refusal's audit row, the
/// idempotency claim's release, the PIN counters) finds none and is never gated.
/// </summary>
public sealed class RequestDeadlineScope
{
    /// <summary>The deadline of the request this scope serves, or null.</summary>
    public IRequestDeadline? Deadline { get; set; }
}

/// <summary>
/// Identity's <see cref="UserManager{TUser}"/> under the request deadline (ADR-0058): every store
/// call it makes takes the deadline's token of the request whose scope built it.
/// </summary>
/// <remarks>
/// <para>
/// WHY A SUBCLASS. <c>AddIdentity</c> registers the base <c>UserManager</c>, whose token is
/// <c>CancellationToken.None</c>; <c>AspNetUserManager</c>, which reads <c>RequestAborted</c>, is
/// not registered. So every call a sign-in, a registration or a PIN set made through Identity (the
/// user's lookup, its password check, its create and update) ran with no token: a sign-in whose
/// lookup was held on the server answered after 20 s under a 2-second deadline, measured by
/// <c>RequestDeadlineSqlServerTests</c> before this class.
/// </para>
/// <para>
/// FROM THE SCOPE'S <see cref="RequestDeadlineScope"/>, read at each call, the holder the commit
/// gate reads. Only the middleware sets it, and only on the request's own scope, so a manager built
/// in a fresh scope, on an exempt endpoint (refresh, revoke, logout) or outside a request finds no
/// deadline and runs with Identity's own token, which nothing cancels, as before (ADR-0058 D4, D12).
/// Not <c>RequestAborted</c> through the HTTP context: a fresh scope opened inside a request would
/// find the request's context there, and its token with it.
/// </para>
/// </remarks>
public sealed class RequestDeadlineUserManager : UserManager<ApplicationUser>
{
    private readonly RequestDeadlineScope? _scope;

    public RequestDeadlineUserManager(
        IUserStore<ApplicationUser> store,
        IOptions<IdentityOptions> optionsAccessor,
        IPasswordHasher<ApplicationUser> passwordHasher,
        IEnumerable<IUserValidator<ApplicationUser>> userValidators,
        IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services,
        ILogger<UserManager<ApplicationUser>> logger)
        : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors,
            services, logger)
    {
        // The scope that built this manager: a host without the deadline registers no holder.
        _scope = services.GetService<RequestDeadlineScope>();
    }

    /// <summary>The deadline's token of this scope's request, or Identity's own when it has none.</summary>
    protected override CancellationToken CancellationToken =>
        _scope?.Deadline is { } deadline ? deadline.Token : base.CancellationToken;
}

/// <summary>
/// The API's request deadline (ADR-0058): a request that is still running
/// <c>RequestDeadline:Seconds</c> after it reached here is cancelled, and answers 503
/// <c>SERVICE_UNAVAILABLE</c> through <c>ServiceUnavailableExceptionHandler</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHY NOT ASP.NET CORE'S <c>RequestTimeouts</c>. It answers only when an
/// <c>OperationCanceledException</c> reaches it, and a cancelled command can surface as a
/// <c>SqlException</c> or anything wrapping one; it clears its feature on the way out, so the handlers
/// could not tell a deadline from a hang-up; and outside <c>UseExceptionHandler</c> the handler's own
/// short-circuit for a cancelled request would swallow it. The commit gate also needs one atomic
/// "refuse, or turn the deadline off" at every commit, which a linked token cannot give.
/// </para>
/// <para>
/// ONE TOKEN FOR THE REQUEST. <c>HttpContext.RequestAborted</c> is replaced by the deadline's token,
/// which the deadline or the client's hang-up cancels, so every service and EF call downstream
/// observes both. <c>UserManager</c> does not read <c>RequestAborted</c>: it takes the same token
/// from <see cref="RequestDeadlineScope"/>, which this middleware sets (see
/// <see cref="RequestDeadlineUserManager"/>). The client's own token is put back on the way out, and
/// the feature stays, so the exception handlers above read the client's token and what the deadline
/// recorded.
/// </para>
/// <para>
/// PLACED INSIDE <c>UseExceptionHandler</c>, before the service credential, authentication and the
/// idempotency claim. Routing has already run (no explicit <c>UseRouting</c>: the host puts it
/// first), so the endpoint's <see cref="NoRequestDeadlineAttribute"/> can be read here.
/// </para>
/// </remarks>
public sealed class RequestDeadlineMiddleware
{
    private readonly RequestDelegate _next;
    private readonly TimeProvider _time;
    private readonly TimeSpan _budget;
    private readonly ILogger<RequestDeadlineMiddleware> _logger;

    public RequestDeadlineMiddleware(
        RequestDelegate next,
        TimeProvider time,
        IOptions<RequestDeadlineOptions> options,
        ILogger<RequestDeadlineMiddleware> logger)
    {
        _next = next;
        _time = time;
        _budget = TimeSpan.FromSeconds(options.Value.Seconds);
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<NoRequestDeadlineAttribute>() is not null)
        {
            await _next(context);
            return;
        }

        var clientAborted = context.RequestAborted;
        var deadline = new RequestDeadline(_time, _budget, clientAborted, _logger);
        context.Features.Set<IRequestDeadline>(deadline);
        context.RequestServices.GetRequiredService<RequestDeadlineScope>().Deadline = deadline;
        context.RequestAborted = deadline.Token;
        try
        {
            await _next(context);
        }
        finally
        {
            deadline.Dispose();
            context.RequestAborted = clientAborted;
        }
    }
}

/// <summary>
/// Turns the request deadline off before any MVC result is written (ADR-0058), so a result that
/// exists is sent: MVC's JSON formatter swallows a cancellation of <c>RequestAborted</c> and would
/// otherwise write an empty or cut body under a status that says success.
/// </summary>
/// <remarks>
/// <para>
/// <c>RequestAborted</c> becomes the client's token again, so a client that hangs up stops the
/// write. Except once a commit has started: then it stays the deadline's token, which nothing can
/// cancel any more, and the response is written whole into the idempotency capture even when the
/// client has gone, so the answer stored for replay is the whole answer.
/// </para>
/// <para>
/// ONE STATE LEAVES A STARTED COMMIT WITH A CANCELLED TOKEN: a commit that failed turned the
/// deadline back on, the deadline then fired, and the save found the commit had landed after all
/// (its check that its audit row is there, asked again under its own budget once the deadline's
/// token stopped the execution strategy's), so no "committed" followed and the action returned
/// its result. That answer is written under no token at all: the request's is
/// cancelled, and the client's could cut the answer stored for replay.
/// </para>
/// </remarks>
public sealed class DeadlineResultFilter : IAsyncAlwaysRunResultFilter
{
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.HttpContext.Features.Get<IRequestDeadline>() is { } deadline && !deadline.Disarm())
        {
            // Not disarmed: no commit started, or the deadline fired after one did (see the remarks).
            context.HttpContext.RequestAborted =
                deadline.CommitEntered ? CancellationToken.None : deadline.ClientAborted;
        }

        return next();
    }
}

/// <summary>
/// Extension methods for <see cref="RequestDeadlineMiddleware"/>.
/// </summary>
public static class RequestDeadlineMiddlewareExtensions
{
    /// <summary>
    /// Adds the request deadline. Inside <c>UseExceptionHandler</c>, before authentication and the
    /// idempotency claim (see <see cref="RequestDeadlineMiddleware"/>).
    /// </summary>
    public static IApplicationBuilder UseRequestDeadline(this IApplicationBuilder builder) =>
        builder.UseMiddleware<RequestDeadlineMiddleware>();
}
