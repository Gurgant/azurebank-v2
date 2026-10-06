using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;

namespace AzureBank.Api.Middleware;

/// <summary>
/// Reads and throws away an oversized request body before its 413, or marks the answer
/// <c>Connection: close</c> when it cannot. One implementation for its two callers:
/// <see cref="IdempotencyMiddleware"/>, for the four money endpoints, and
/// <c>[RefuseOversizedBody]</c>, for the four authorisation mints. Each passes the host's
/// <see cref="TimeProvider"/>, so a test's clock runs the five seconds. The measurements in the
/// remarks below were taken on the money endpoints, before the mints called this.
/// </summary>
internal sealed class OversizedBodyDrain(TimeProvider timeProvider)
{
    private readonly TimeProvider _timeProvider = timeProvider;

    // An oversized body up to this size is read and thrown away before the 413, so the refusal
    // does not close the connection under a sender that is still writing (see DrainOrCloseAsync).
    private const long DrainCapBytes = 1_048_576;

    // How long the drain waits for the rest of such a body: the five seconds Kestrel gives its own
    // drain of a body the app left unread. A body not in by then gets its 413 without the rest, and
    // Connection: close (DrainOrCloseAsync).
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Makes the early 413 safe for the connection it is answered on. Refused without being read,
    /// the body was left for Kestrel, which drained it to keep the connection alive, hit the
    /// endpoint's 32 KB limit and aborted the connection: under a proxy still sending the body
    /// (YARP in the BFF: a 502, or a reset passed on to the client), or after the proxy had pooled
    /// it for the next request (a 502 for a request that did nothing wrong). Measured 2026-09-24
    /// through the BFF: 3 of 42 runs of the contract suite's oversized-body test failed those
    /// ways; with the body drained first, 0 of 30. It is read and thrown away, never buffered or
    /// hashed, so ADR-0009's reason for refusing early holds. Where the server enforces a size
    /// limit (Kestrel: the endpoint's 32 KB) it is raised to the cap first; a server that offers
    /// none, like the in-memory test host, has none to raise. And it is read for five seconds at
    /// most, the time Kestrel gives its own drain: with no deadline, a sender trickling the body
    /// held the request until its last byte -- measured the same day, a 60 KB body sent at 1 KB/s
    /// got its 413 after 60 s, and at Kestrel's minimum rate of 240 bytes a second 1 MiB would
    /// take 73 minutes. Above the cap, or with a limit that can no longer be raised, the body is
    /// not read at all, and once the five seconds are up the rest is not waited for: either way
    /// the 413 says <c>Connection: close</c>, so no proxy reuses a connection that is about to go.
    /// Measured on the trickled body: the 413 at 5.07 s, then Kestrel's own drain had the rest
    /// for five seconds more and reset the connection at 10.74 s.
    /// </summary>
    internal async Task DrainOrCloseAsync(HttpContext context)
    {
        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (context.Request.ContentLength <= DrainCapBytes && limit is not { IsReadOnly: true })
        {
            if (limit is not null)
            {
                limit.MaxRequestBodySize = DrainCapBytes;
            }

            // The client's token, for the reason the body's hash is read under it: a read cancelled
            // by its token leaves the reader mid-read. The five seconds below bound it instead.
            if (await DrainInTimeAsync(context.Request.BodyReader, IdempotencyMiddleware.ClientAbortedOf(context)))
            {
                return;
            }
        }

        context.Response.OnStarting(static state =>
        {
            ((HttpResponse)state).Headers.Connection = "close";
            return Task.CompletedTask;
        }, context.Response);
    }

    /// <summary>
    /// Reads the body to its end and throws it away, for <see cref="DrainTimeout"/> at most: true
    /// when all of it came in time. The deadline stops the read with <c>CancelPendingRead</c>, not
    /// with a cancelled token: a read cancelled by its token leaves Kestrel's body reader mid-read,
    /// and Kestrel's own drain of the rest then fails on it and logs an error ("Reading is already
    /// in progress", measured 2026-09-24).
    /// </summary>
    private async Task<bool> DrainInTimeAsync(PipeReader body, CancellationToken aborted)
    {
        using var deadline = new CancellationTokenSource(DrainTimeout, _timeProvider);
        ReadResult result;
        using (deadline.Token.Register(static reader => ((PipeReader)reader!).CancelPendingRead(), body))
        {
            do
            {
                result = await body.ReadAsync(aborted);
                body.AdvanceTo(result.Buffer.End);
            }
            while (!result.IsCompleted && !result.IsCanceled);
        }

        // Checked once the registration is gone, so a cancel already under way has landed. A
        // deadline that fired as the last bytes came in closes the connection too: its cancel may
        // still be pending, and the next read on this connection would be Kestrel's own.
        return result.IsCompleted && !deadline.IsCancellationRequested;
    }
}
