using System.Diagnostics;
using AzureBank.Api.Attributes;
using AzureBank.Api.Middleware;
using AzureBank.Api.Observability;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Storage;

namespace AzureBank.Api.Handlers;

/// <summary>
/// A client that hung up gets nothing (ADR-0058): no body, no 503 and no Error line. Registered
/// FIRST, before every other handler.
/// </summary>
/// <remarks>
/// <para>
/// <c>UseExceptionHandler</c> already short-circuits a cancelled request to 499, but only for an
/// <c>OperationCanceledException</c> or an <c>IOException</c>. With the request's token reaching
/// every database call, a hang-up also surfaces as what SqlClient and EF make of a cancelled command:
/// a <c>SqlException</c>, a <c>DbUpdateException</c>, a retry limit. Those reached the handlers below
/// and were answered 500 at Error, with a stack trace, to a client that was gone, and writing to it
/// could throw as well ("error handler threw").
/// </para>
/// <para>
/// The token read here is the client's own: the request deadline puts it back on its way out, so a
/// request the DEADLINE cancelled is not mistaken for a hang-up.
/// </para>
/// <para>
/// A CLIENT THAT HAS GONE DID NOT NECESSARILY CAUSE THE FAILURE. The exempt endpoints (refresh,
/// revoke, logout) never hand the client's token to a call, and after a commit has started nothing
/// cancels a request, so a database that fails then fails whether or not the client is still there.
/// Nothing is written either way, but only a failure the hang-up caused is logged at Debug: the
/// deadline says the client cancelled the request before any commit started, or the chain holds
/// the cancellation itself.
/// Anything else is logged at Warning with the SQL error numbers and the chain the outage 503 would
/// have named, so an outage nobody was answered is not lost.
/// </para>
/// </remarks>
public sealed class ClientAbortedExceptionHandler : IExceptionHandler
{
    private readonly ILogger<ClientAbortedExceptionHandler> _logger;
    private readonly ReceivedAtClock _clock;

    public ClientAbortedExceptionHandler(ILogger<ClientAbortedExceptionHandler> logger, ReceivedAtClock clock)
    {
        _logger = logger;
        _clock = clock;
    }

    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!httpContext.RequestAborted.IsCancellationRequested)
        {
            return ValueTask.FromResult(false);
        }

        if (CausedByHangUp(httpContext.Features.Get<IRequestDeadline>(), exception))
        {
            _logger.LogDebug(
                "The client closed the request on {RoutePattern} ({ExceptionType}); nothing was written",
                RequestLogRoute.Of(httpContext), exception.GetType().Name);
        }
        else
        {
            var received = httpContext.Features.Get<ReceivedAtFeature>()?.Value;
            _logger.LogWarning(
                exception,
                "The client closed the request on {RoutePattern}, which failed after {ElapsedMs} ms for "
                + "another cause; nothing was written; SQL errors {SqlErrorNumbers}; chain {ExceptionTypes}",
                RequestLogRoute.Of(httpContext),
                received is { } at ? (long)(_clock.Now - at).TotalMilliseconds : -1,
                ServiceUnavailableExceptionHandler.SqlErrorNumbers(exception),
                ServiceUnavailableExceptionHandler.ExceptionTypes(exception));

            // As the outage 503 does: this handler marks the exception handled, so the
            // instrumentation would otherwise leave the span without it.
            Activity.Current?.AddException(exception);
        }

        if (!httpContext.Response.HasStarted)
        {
            httpContext.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }

        return ValueTask.FromResult(true);
    }

    /// <summary>
    /// True when the client hanging up caused <paramref name="exception"/>: the request deadline
    /// recorded the client as what cancelled the request before any commit started, or the chain
    /// holds a cancellation.
    /// </summary>
    /// <remarks>
    /// Once a commit has started, a hang-up is recorded only after a commit failed
    /// (<see cref="IRequestDeadline.CommitFailed"/>): the failure came first and is the database's.
    /// </remarks>
    internal static bool CausedByHangUp(IRequestDeadline? deadline, Exception exception)
    {
        if (deadline is { FiredByClient: true, CommitEntered: false })
        {
            return true;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The outage answer (ADR-0058): when the database cannot be reached, or the request ran past its
/// deadline, the API answers 503 <c>SERVICE_UNAVAILABLE</c> with <c>retryAfterSeconds</c>, a
/// <c>Retry-After</c> header and <c>Cache-Control: no-store</c>, and logs a Warning that names every
/// SQL error number in the exception chain. Before, each of these was a 500 through
/// <see cref="GlobalExceptionHandler"/>, which reads as a bug, and the numbers were logged nowhere.
/// </summary>
/// <remarks>
/// <para>
/// THE MAPPING WALKS THE WHOLE INNER-EXCEPTION CHAIN: EF wraps what failed. With retries on, a fault
/// that persists arrives as <c>RetryLimitExceededException</c>; without, a transient one arrives as
/// EF's <c>InvalidOperationException</c> "likely due to a transient failure" with the fault inside;
/// a save wraps its <c>SqlException</c> in a <c>DbUpdateException</c>.
/// </para>
/// <para>
/// 503 FOR: a request the deadline cancelled, whatever it threw; a retry limit; a
/// <c>SqlException</c> that EF's own detector calls transient, or with a number in
/// {-2, 35, 11001, 18401, 17197, 17142}, or of class 20 or more (a fatal, connection-level error: a
/// refused connection reads as 258, class 20, on Windows); SqlClient's pool-wait timeout, an
/// <c>InvalidOperationException</c> matched by its message because nothing else distinguishes it; a
/// bare <c>TimeoutException</c>. EVERYTHING ELSE STAYS A 500: a unique violation, a concurrency
/// conflict, the application lock's <c>THROW 50000</c>.
/// </para>
/// <para>
/// -2, A COMMAND TIMEOUT, IS A 503 TOO: the database did not answer in time. That includes the audit
/// chain's bounded tail read (ADR-0044), which answered 500 before; the application lock's own
/// timeout is a raised error, 50000, and stays 500.
/// </para>
/// <para>
/// <c>applied: false</c> ONLY WHEN THIS ANSWER KNOWS IT, and then the detail says so too: on the four
/// money endpoints (<see cref="RequireIdempotencyAttribute"/>), for a request that owns the
/// idempotency claim it made (<see cref="OwnedIdempotencyClaim"/>) and has let no commit start
/// (<see cref="IRequestDeadline.CommitEntered"/>). A money request moves money only inside one
/// transaction of its own context, and every such commit passes the commit gate
/// (<c>RequestDeadlineSqlServerTests</c> counts one per success), so no commit started means no money
/// moved. Anywhere else the key is left out, never set to true or to a guess: a commit that started
/// may have landed even if it failed, and a request that failed before owning its claim cannot know
/// what an earlier request with the same key did. A client that keeps its key on a 503 is safe
/// either way; one told "nothing was applied" when something was would pay again under a new key.
/// </para>
/// <para>
/// Registered after the validation and domain handlers and before the global one, so a domain
/// refusal keeps its own status and only what would have been a 500 is examined here.
/// </para>
/// </remarks>
public sealed class ServiceUnavailableExceptionHandler : IExceptionHandler
{
    /// <summary>The request deadline cancelled the request.</summary>
    public const string Deadline = "Deadline";

    /// <summary>EF retried a transient failure as often as its budget allows.</summary>
    public const string RetryLimitExceeded = "RetryLimitExceeded";

    /// <summary>A command ran into its own timeout (SQL error -2).</summary>
    public const string CommandTimeout = "CommandTimeout";

    /// <summary>The database refused, dropped or could not open the connection.</summary>
    public const string DatabaseUnreachable = "DatabaseUnreachable";

    /// <summary>No pooled connection came free within the connect timeout.</summary>
    public const string PoolExhausted = "PoolExhausted";

    /// <summary>A bare timeout, not a SQL error.</summary>
    public const string Timeout = "Timeout";

    /// <summary>The answer's detail: no time, no cause, nothing the visitor can act on but a retry.</summary>
    internal const string Detail = "The service is temporarily unavailable. Try again shortly.";

    /// <summary>The detail of an answer that knows nothing was applied (<c>applied: false</c>).</summary>
    internal const string NothingAppliedDetail =
        "The service is temporarily unavailable, and nothing was changed. Try again shortly.";

    /// <summary>SQL errors that mean the database cannot be reached, beyond EF's transient list.</summary>
    private static readonly HashSet<int> UnreachableNumbers = [35, 11001, 18401, 17197, 17142];

    /// <summary>
    /// SqlClient's text for a pool wait that ran out. Its exception is an
    /// <c>InvalidOperationException</c>, which is otherwise a bug, so only the text tells them apart
    /// (measured: "Timeout expired.  The timeout period elapsed prior to obtaining a connection from
    /// the pool. ...").
    /// </summary>
    /// <remarks>
    /// ENGLISH ONLY BECAUSE <c>Program.cs</c> PINS THE UI CULTURE. SqlClient translates the message
    /// through satellite assemblies (the Italian one reads "Timeout scaduto. Il tempo disponibile è
    /// scaduto prima di aver ottenuto la connessione dal pool."), so on a host with another UI
    /// language this text would not match and the timeout would be a 500.
    /// </remarks>
    private const string PoolTimeoutText = "prior to obtaining a connection from the pool";

    private readonly ILogger<ServiceUnavailableExceptionHandler> _logger;
    private readonly ReceivedAtClock _clock;

    public ServiceUnavailableExceptionHandler(
        ILogger<ServiceUnavailableExceptionHandler> logger, ReceivedAtClock clock)
    {
        _logger = logger;
        _clock = clock;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var deadline = httpContext.Features.Get<IRequestDeadline>();
        if (ReasonFor(exception, deadline?.FiredByDeadline == true) is not { } reason)
        {
            return false;
        }

        var received = httpContext.Features.Get<ReceivedAtFeature>()?.Value;
        _logger.LogWarning(
            exception,
            "Service unavailable ({Reason}) on {RoutePattern} after {ElapsedMs} ms; "
            + "SQL errors {SqlErrorNumbers}; chain {ExceptionTypes}",
            reason,
            RequestLogRoute.Of(httpContext),
            received is { } at ? (long)(_clock.Now - at).TotalMilliseconds : -1,
            SqlErrorNumbers(exception),
            ExceptionTypes(exception));

        // As GlobalExceptionHandler does, and for its reason: this handler marks the exception
        // handled, so the instrumentation would otherwise leave the span without it.
        Activity.Current?.AddException(exception);

        var details = new Dictionary<string, object>
        {
            ["retryAfterSeconds"] = ServiceUnavailableException.OutageRetryAfterSeconds,
        };
        var nothingApplied = NothingApplied(httpContext, deadline);
        if (nothingApplied)
        {
            details["applied"] = false;
        }

        await AppExceptionHandler.WriteProblemAsync(
            httpContext,
            StatusCodes.Status503ServiceUnavailable,
            ErrorCodes.ServiceUnavailable,
            nothingApplied ? NothingAppliedDetail : Detail,
            details,
            cancellationToken);

        return true;
    }

    /// <summary>
    /// True when the answer may say <c>applied: false</c>: a money endpoint, whose request owns the
    /// idempotency claim it made and has let no commit start (see the remarks on the class).
    /// </summary>
    /// <remarks>
    /// The endpoint is read from the feature the exception handler leaves behind, since it nulls the
    /// request's own before it runs its handlers. No deadline, no answer: without one the request
    /// cannot tell whether a commit started (no money endpoint is exempt from it).
    /// </remarks>
    internal static bool NothingApplied(HttpContext httpContext, IRequestDeadline? deadline) =>
        (httpContext.GetEndpoint() ?? httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint)?
            .Metadata.GetMetadata<RequireIdempotencyAttribute>() is not null
        && httpContext.Features.Get<OwnedIdempotencyClaim>() is not null
        && deadline is { CommitEntered: false };

    /// <summary>
    /// Why <paramref name="exception"/> is the outage 503, or null when it is not one.
    /// </summary>
    /// <param name="exception">What reached the handlers.</param>
    /// <param name="firedByDeadline">
    /// True when the request deadline cancelled the request: then any exception is its consequence
    /// (a cancelled command surfaces as whatever SqlClient and EF make of it).
    /// </param>
    internal static string? ReasonFor(Exception exception, bool firedByDeadline)
    {
        if (firedByDeadline)
        {
            return Deadline;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case RetryLimitExceededException:
                    return RetryLimitExceeded;

                case SqlException sql when Numbers(sql).Contains(-2):
                    return CommandTimeout;

                case SqlException sql when IsUnreachable(sql):
                    return DatabaseUnreachable;

                case InvalidOperationException when current.Message.Contains(PoolTimeoutText, StringComparison.Ordinal):
                    return PoolExhausted;

                case TimeoutException:
                    return Timeout;
            }
        }

        return null;
    }

    private static bool IsUnreachable(SqlException sql) =>
        IsTransientToEf(sql)
        || Numbers(sql).Any(UnreachableNumbers.Contains)
        || sql.Errors.Cast<SqlError>().Any(e => e.Class >= 20)
        || sql.Class >= 20;

    /// <summary>
    /// EF's own transient list, asked directly so this one cannot drift from what EF retries: every
    /// number it would retry, answered after its budget, is an outage.
    /// </summary>
    private static bool IsTransientToEf(SqlException sql)
    {
        // EF1001: the detector lives in an Internal namespace. It is the list EnableRetryOnFailure
        // retries on, and copying it here would let the two drift; the handler's unit tests pin the
        // numbers this relies on (4060, 40613, 11001, 1205 to 503; 2627 to 500).
#pragma warning disable EF1001
        return Microsoft.EntityFrameworkCore.SqlServer.Storage.Internal.SqlServerTransientExceptionDetector
            .ShouldRetryOn(sql);
#pragma warning restore EF1001
    }

    private static IEnumerable<int> Numbers(SqlException sql) =>
        sql.Errors.Cast<SqlError>().Select(e => e.Number).Append(sql.Number);

    /// <summary>
    /// Every <c>SqlError.Number</c> of every <c>SqlException</c> in the chain, in order:
    /// <c>SqlException.Number</c> is only the first of its errors.
    /// </summary>
    internal static int[] SqlErrorNumbers(Exception exception)
    {
        var numbers = new List<int>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                numbers.AddRange(sql.Errors.Cast<SqlError>().Select(e => e.Number));
            }
        }

        return [.. numbers];
    }

    /// <summary>The type names of the chain, outermost first: "RetryLimitExceededException > SqlException".</summary>
    internal static string ExceptionTypes(Exception exception)
    {
        var names = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            names.Add(current.GetType().Name);
        }

        return string.Join(" > ", names);
    }
}
