using System.Diagnostics;

namespace AzureBank.Api.Middleware;

/// <summary>
/// The instant the API received a request, on ONE clock for the whole process: the UTC time when
/// the clock was created plus a <see cref="Stopwatch"/> since then (06 §4.3, F10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>DateTime.UtcNow</c>.</b> The tripwire compares two of these stamps: the one a
/// revoke writes as <c>RevokedAt</c> and the one a later renewal of the same grant carries. A
/// wall-clock step between the two (an NTP correction, a VM resumed from a pause) could put the
/// renewal "before" the revoke that preceded it, or the other way round, and the second is a false
/// tripwire. The stopwatch only moves forward, so two stamps from one process keep the order their
/// requests arrived in. Stamps from two processes (two test hosts, or a restart) are each anchored to
/// the wall clock when their clock was made, so they agree to within that clock's drift.
/// </para>
/// <para>
/// <b>What it is not.</b> The time a grant is issued and when it expires stay on the wall clock
/// (<c>RefreshToken.IsExpired</c>): a lifetime is measured in minutes, where the drift is nothing,
/// and it must agree with the cleanup sweep's clock.
/// </para>
/// </remarks>
public sealed class ReceivedAtClock
{
    /// <summary>The one clock of this process, registered as a singleton by the API.</summary>
    public static ReceivedAtClock ForThisProcess { get; } = new();

    private readonly DateTime _originUtc = DateTime.UtcNow;
    private readonly long _originTimestamp = Stopwatch.GetTimestamp();

    /// <summary>Now, in UTC, on this process's monotonic clock.</summary>
    public DateTime Now => _originUtc + Stopwatch.GetElapsedTime(_originTimestamp);
}

/// <summary>The <see cref="ReceivedAtClock"/> reading taken when the request arrived.</summary>
/// <param name="Value">UTC.</param>
public sealed record ReceivedAtFeature(DateTime Value);

/// <summary>
/// Stamps every request with <see cref="ReceivedAtClock"/> before anything else can delay it
/// (06 §4.3): the FIRST middleware in the pipeline.
/// </summary>
/// <remarks>
/// First, because the stamp orders a renewal against a revoke of the same grant, and a stamp taken
/// after the correlation, logging and exception middleware would carry their delays too. Under a
/// database hang those are nothing, but under a thread-pool stall they are not, and nothing about
/// taking the stamp costs anything.
/// </remarks>
public sealed class ReceivedAtMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ReceivedAtClock _clock;

    public ReceivedAtMiddleware(RequestDelegate next, ReceivedAtClock clock)
    {
        _next = next;
        _clock = clock;
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Features.Set(new ReceivedAtFeature(_clock.Now));
        return _next(context);
    }
}

/// <summary>Registration and reading of the <see cref="ReceivedAtFeature"/>.</summary>
public static class ReceivedAtMiddlewareExtensions
{
    /// <summary>Stamps each request with the instant it arrived. Register it first.</summary>
    public static IApplicationBuilder UseReceivedAtStamp(this IApplicationBuilder builder) =>
        builder.UseMiddleware<ReceivedAtMiddleware>();

    /// <summary>
    /// The instant this request arrived. Throws when the stamp is missing: a caller that silently
    /// used "now" instead would write a <c>RevokedAt</c> no renewal can be compared with.
    /// </summary>
    public static DateTime ReceivedAt(this HttpContext context) =>
        context.Features.Get<ReceivedAtFeature>()?.Value
        ?? throw new InvalidOperationException(
            "The request has no ReceivedAt stamp: UseReceivedAtStamp must run before it.");
}
