using System.Buffers;
using System.IO.Pipelines;
using System.Security.Claims;
using System.Text;
using AzureBank.Api.Attributes;
using AzureBank.Api.Middleware;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace AzureBank.Tests.Unit.Middleware;

/// <summary>
/// What the idempotency middleware does around the request it guards, when the database or the
/// client fails it (ADR-0009, ADR-0058): whose claim it is, what it stores for replay, how long its
/// own bookkeeping may take, and under which token an answer that exists is sent.
/// </summary>
/// <remarks>
/// On a clock the test moves and a mocked store, so a budget is proved by running out rather than by
/// waiting for it. The SQL Server proofs hold the real statements (<c>RequestDeadlineSqlServerTests</c>).
/// </remarks>
public class IdempotencyMiddlewareTests
{
    private const string Answer = """{"data":{"newBalance":150.0}}""";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    /// <summary>Long enough for a step on another thread, far shorter than an unbounded wait.</summary>
    private static readonly TimeSpan Settles = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new();
    private readonly RecordingLoggerProvider _log = new();
    private readonly Mock<IIdempotencyService> _store = new();

    public IdempotencyMiddlewareTests()
    {
        _store.Setup(s => s.ComputeRequestHashAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("hash");
        Acquires(replay: false);
    }

    [Fact]
    public async Task AClaimThisRequestMade_MarksTheRequestAsItsOwner()
    {
        var context = Context();
        OwnedIdempotencyClaim? seen = null;

        await Middleware(c =>
        {
            seen = c.Features.Get<OwnedIdempotencyClaim>();
            return Created(c);
        }).InvokeAsync(context, _store.Object);

        seen.Should().NotBeNull("from its claim on, the request knows that nothing under the key was applied before it");
    }

    [Fact]
    public async Task AReplay_DoesNotMarkTheRequest()
    {
        Acquires(replay: true);
        var context = Context();

        await Middleware(_ => throw new InvalidOperationException("a replay runs no action")).InvokeAsync(context, _store.Object);

        context.Features.Get<OwnedIdempotencyClaim>().Should().BeNull(
            "the key's first request moved the money: a failure now must never say nothing was applied");
    }

    [Fact]
    public async Task AnEmptySuccess_IsNeverStoredForReplay()
    {
        // MVC's formatter writes nothing under a success status when its token is cancelled. Stored,
        // that nothing was every retry's answer; not stored, the record stays Executed and a retry
        // gets IN_FLIGHT, then RESULT_UNKNOWN.
        var context = Context();

        await Middleware(c =>
        {
            c.Response.StatusCode = StatusCodes.Status201Created;
            return Task.CompletedTask;
        }).InvokeAsync(context, _store.Object);

        _store.Verify(
            s => s.CompleteAsync(It.IsAny<IdempotencyRecord>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
        _log.Lines.Should().Contain(
            l => l.Level == LogLevel.Warning && l.Message.Contains("empty success", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StoringTheAnswer_GivesUpAfterThreeSeconds_AndTheAnswerIsStillSent()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.Setup(s => s.CompleteAsync(
                It.IsAny<IdempotencyRecord>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((IdempotencyRecord _, int _, string? _, string _, CancellationToken token) =>
            {
                entered.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            });
        var context = Context();

        var running = Middleware(Created).InvokeAsync(context, _store.Object);
        await entered.Task.WaitAsync(Settles);
        _time.Advance(Budget - TimeSpan.FromMilliseconds(1));
        running.IsCompleted.Should().BeFalse("the store still has its three seconds");
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await running.WaitAsync(Settles);

        context.Response.StatusCode.Should().Be(StatusCodes.Status201Created, "the deposit committed: its answer is true");
        ComparableText.Of(ResponseText(context)).Should().Be(ComparableText.Of(Answer), "and it is sent whole");
    }

    [Fact]
    public async Task ReleasingTheClaim_GivesUpAfterThreeSeconds_AndTheFailureStillReachesTheHandlers()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.Setup(s => s.ReleaseIfNotExecutedAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string _, Guid _, Guid _, CancellationToken token) =>
            {
                entered.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            });
        var outage = new TimeoutException("the database did not answer");
        var context = Context();

        var running = Middleware(_ => throw outage).InvokeAsync(context, _store.Object);
        await entered.Task.WaitAsync(Settles);
        _time.Advance(Budget - TimeSpan.FromMilliseconds(1));
        running.IsCompleted.Should().BeFalse("the release still has its three seconds");
        _time.Advance(TimeSpan.FromMilliseconds(1));

        (await Record.ExceptionAsync(() => running.WaitAsync(Settles))).Should().BeSameAs(
            outage, "the outage is answered as soon as the release gives up, and a failed release never masks it");
    }

    [Fact]
    public async Task AReplay_IsSentWhole_EvenWhenTheDeadlineFiredAsItsKeyWasRead()
    {
        // The key was read just in time. A stored answer exists and is true, so it is sent under the
        // client's token: only the client going away stops it.
        Acquires(replay: true);
        using var client = new CancellationTokenSource();
        using var deadline = new RequestDeadline(_time, TimeSpan.FromSeconds(40), client.Token, NullLogger.Instance);
        var context = Context(deadline);
        _time.Advance(TimeSpan.FromSeconds(40));
        context.RequestAborted.IsCancellationRequested.Should().BeTrue("the proof is void unless the deadline has fired");

        await Middleware(_ => throw new InvalidOperationException("a replay runs no action")).InvokeAsync(context, _store.Object);

        context.Response.StatusCode.Should().Be(StatusCodes.Status201Created);
        context.Response.Headers[IdempotencyConstants.ReplayedHeaderName].ToString().Should().Be("true");
        ComparableText.Of(ResponseText(context)).Should().Be(ComparableText.Of(Answer));
    }

    [Fact]
    public async Task AnAnswerStoredAfterTheClientHungUp_IsNotWrittenToTheClient()
    {
        // Once a commit has started nothing cancels the request's token, so the answer is written
        // whole into the capture and stored for the retry; copying it to a client that has gone
        // stops at once, and the exception handler answers that with nothing (499).
        using var client = new CancellationTokenSource();
        using var deadline = new RequestDeadline(_time, TimeSpan.FromSeconds(40), client.Token, NullLogger.Instance);
        var context = Context(deadline);

        var running = Middleware(async c =>
        {
            deadline.TryEnterCommit().Should().BeTrue();
            deadline.Committed();
            await client.CancelAsync();
            await Created(c);
        }).InvokeAsync(context, _store.Object);

        (await Record.ExceptionAsync(() => running.WaitAsync(Settles))).Should().BeAssignableTo<OperationCanceledException>();
        _store.Verify(
            s => s.CompleteAsync(It.IsAny<IdempotencyRecord>(), StatusCodes.Status201Created, It.IsAny<string?>(), Answer, It.IsAny<CancellationToken>()),
            Times.Once(), "the retry of a client that hung up after the commit is replayed the whole answer");
        context.Response.Body.Length.Should().Be(0, "nothing is written to a client that is not there");
    }

    [Fact]
    public async Task TheBody_IsReadUnderTheClientsToken_AndTheClaimUnderTheDeadlines()
    {
        // A read of the body that the deadline cancels leaves Kestrel's body reader mid-read, and its
        // drain of the rest then fails on it and closes the connection. Only the client going away
        // may stop the read; the deadline still cancels the claim that follows.
        using var client = new CancellationTokenSource();
        using var deadline = new RequestDeadline(_time, TimeSpan.FromSeconds(40), client.Token, NullLogger.Instance);
        var context = Context(deadline);

        await Middleware(Created).InvokeAsync(context, _store.Object);

        _store.Verify(s => s.ComputeRequestHashAsync(It.IsAny<Stream>(), client.Token), Times.Once());
        _store.Verify(
            s => s.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), deadline.Token),
            Times.Once());
    }

    [Fact]
    public async Task AnOversizedBody_IsDrainedUnderTheClientsToken()
    {
        using var client = new CancellationTokenSource();
        using var deadline = new RequestDeadline(_time, TimeSpan.FromSeconds(40), client.Token, NullLogger.Instance);
        var context = Context(deadline);
        context.Request.ContentLength = 40_000;
        var body = new RecordingBodyReader();
        context.Features.Set<IRequestBodyPipeFeature>(body);

        var refused = await Record.ExceptionAsync(() => Middleware(Created).InvokeAsync(context, _store.Object));

        refused.Should().BeOfType<IdempotencyException>("an oversized body is refused with its 413");
        body.ReadUnder.Should().Equal([client.Token], "the drain reads to the end unless the client goes away");
    }

    /// <summary>A request body that has already ended, and records the token each read was given.</summary>
    private sealed class RecordingBodyReader : PipeReader, IRequestBodyPipeFeature
    {
        public List<CancellationToken> ReadUnder { get; } = [];

        public PipeReader Reader => this;

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadUnder.Add(cancellationToken);
            return ValueTask.FromResult(new ReadResult(ReadOnlySequence<byte>.Empty, isCanceled: false, isCompleted: true));
        }

        public override bool TryRead(out ReadResult result)
        {
            result = new ReadResult(ReadOnlySequence<byte>.Empty, isCanceled: false, isCompleted: true);
            return true;
        }

        public override void AdvanceTo(SequencePosition consumed)
        {
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
        }

        public override void CancelPendingRead()
        {
        }

        public override void Complete(Exception? exception = null)
        {
        }
    }

    private void Acquires(bool replay)
    {
        var record = new IdempotencyRecord
        {
            UserId = Guid.NewGuid(),
            Endpoint = "POST api/transactions/deposit",
            Key = Guid.NewGuid(),
            ClaimId = Guid.NewGuid(),
            RequestHash = "hash",
            Status = replay ? IdempotencyStatus.Completed : IdempotencyStatus.Processing,
            ResponseStatusCode = replay ? StatusCodes.Status201Created : null,
            ResponseContentType = replay ? "application/json; charset=utf-8" : null,
            ResponseBody = replay ? Answer : null,
        };

        _store.Setup(s => s.TryAcquireAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdempotencyAcquisition { Record = record, IsReplay = replay });
    }

    private static async Task Created(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(Answer));
    }

    private IdempotencyMiddleware Middleware(RequestDelegate next) =>
        new(next, new LoggerFactory([_log]).CreateLogger<IdempotencyMiddleware>(), _time);

    /// <summary>
    /// A deposit with its key, as the middleware receives it: under the deadline's token and with its
    /// feature when one is given, as the request deadline sets them up.
    /// </summary>
    private static DefaultHttpContext Context(RequestDeadline? deadline = null)
    {
        var body = Encoding.UTF8.GetBytes("""{"accountId":"00000000-0000-0000-0000-000000000001","amount":50}""");
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")),
        };
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("api/transactions/deposit"),
            0,
            new EndpointMetadataCollection(new RequireIdempotencyAttribute()),
            "deposit"));
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers[IdempotencyConstants.HeaderName] = Guid.NewGuid().ToString();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();

        if (deadline is not null)
        {
            context.Features.Set<IRequestDeadline>(deadline);
            context.RequestAborted = deadline.Token;
        }

        return context;
    }

    private static string ResponseText(HttpContext context) =>
        Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
}
