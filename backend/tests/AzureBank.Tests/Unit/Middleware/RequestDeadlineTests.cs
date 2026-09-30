using System.Reflection;
using AzureBank.Api.Middleware;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AzureBank.Tests.Unit.Middleware;

/// <summary>
/// The request deadline's state machine (ADR-0058), on a clock the test moves: the deadline and a
/// commit decide once, under one lock, which of them happens, and a commit that started is never
/// cancelled.
/// </summary>
public class RequestDeadlineTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private readonly FakeTimeProvider _time = new();

    private RequestDeadline Deadline(CancellationToken clientAborted = default) =>
        new(_time, Budget, clientAborted, NullLogger.Instance);

    [Fact]
    public void TheDeadline_CancelsTheRequest_AndNoCommitStartsAfterIt()
    {
        using var deadline = Deadline();

        _time.Advance(Budget);

        deadline.Token.IsCancellationRequested.Should().BeTrue();
        deadline.FiredByDeadline.Should().BeTrue();
        deadline.TryEnterCommit().Should().BeFalse("a request that has been cancelled commits nothing afterwards");
        deadline.CommitEntered.Should().BeFalse();
    }

    [Fact]
    public void ACommitThatStarted_IsNeverCancelled_ByTheDeadline()
    {
        using var deadline = Deadline();
        _time.Advance(Budget - TimeSpan.FromSeconds(1));

        deadline.TryEnterCommit().Should().BeTrue();
        _time.Advance(TimeSpan.FromSeconds(30));

        deadline.Token.IsCancellationRequested.Should().BeFalse("the commit is running: its answer must be sent");
        deadline.FiredByDeadline.Should().BeFalse();

        deadline.Committed();
        _time.Advance(TimeSpan.FromMinutes(5));
        deadline.Token.IsCancellationRequested.Should().BeFalse("it committed: the rest of the request is answered");
    }

    [Fact]
    public void ACommitThatStarted_IsNeverCancelled_ByTheClientHangingUp()
    {
        using var client = new CancellationTokenSource();
        using var deadline = Deadline(client.Token);

        deadline.TryEnterCommit().Should().BeTrue();
        client.Cancel();

        deadline.Token.IsCancellationRequested.Should().BeFalse();
        deadline.FiredByClient.Should().BeFalse();
    }

    [Fact]
    public void AClientHangingUp_CancelsTheRequest_BeforeAnyCommit()
    {
        using var client = new CancellationTokenSource();
        using var deadline = Deadline(client.Token);

        client.Cancel();

        deadline.Token.IsCancellationRequested.Should().BeTrue();
        deadline.FiredByClient.Should().BeTrue();
        deadline.FiredByDeadline.Should().BeFalse("the handlers answer a hang-up with nothing, not with a 503");
        deadline.TryEnterCommit().Should().BeFalse();
    }

    [Fact]
    public void AFailedCommit_TurnsTheDeadlineBackOn_AtItsOriginalInstant()
    {
        using var deadline = Deadline();
        _time.Advance(TimeSpan.FromSeconds(30));
        deadline.TryEnterCommit().Should().BeTrue();

        deadline.CommitFailed();
        _time.Advance(TimeSpan.FromSeconds(9));
        deadline.Token.IsCancellationRequested.Should().BeFalse("39 s: the deadline is at 40");

        _time.Advance(TimeSpan.FromSeconds(1));
        deadline.Token.IsCancellationRequested.Should().BeTrue("a failed commit buys the request no time");
        deadline.FiredByDeadline.Should().BeTrue();
        deadline.CommitEntered.Should().BeTrue("a commit that failed may have landed: never 'nothing was applied'");
    }

    [Fact]
    public void AFailedCommit_PastTheDeadline_CancelsAtOnce()
    {
        using var deadline = Deadline();
        deadline.TryEnterCommit().Should().BeTrue();
        _time.Advance(TimeSpan.FromSeconds(50));

        deadline.CommitFailed();
        _time.Advance(TimeSpan.Zero);

        deadline.Token.IsCancellationRequested.Should().BeTrue();
        deadline.TryEnterCommit().Should().BeFalse("the strategy's retry of the commit must not start either");
    }

    [Fact]
    public void AFailedCommit_AfterTheClientHungUp_CancelsTheRequest()
    {
        using var client = new CancellationTokenSource();
        using var deadline = Deadline(client.Token);
        deadline.TryEnterCommit().Should().BeTrue();
        client.Cancel();

        deadline.CommitFailed();

        deadline.Token.IsCancellationRequested.Should().BeTrue("the hang-up was ignored only while the commit ran");
        deadline.FiredByClient.Should().BeTrue();
    }

    [Fact]
    public void Disarming_KeepsTheRequestToken_OnlyOnceACommitHasStarted()
    {
        using var read = Deadline();
        read.Disarm().Should().BeFalse("no commit: the result is written under the client's own token");
        _time.Advance(Budget);
        read.Token.IsCancellationRequested.Should().BeFalse("a result that exists is sent");

        using var write = Deadline();
        write.TryEnterCommit().Should().BeTrue();
        write.Committed();
        write.Disarm().Should().BeTrue("a commit started: the answer is written whole, for the replay store");

        using var late = Deadline();
        _time.Advance(Budget);
        late.Disarm().Should().BeFalse("fired: its token is cancelled, so the result goes out under the client's");
    }

    [Fact]
    public void ATimerThatFiresAfterTheRequestEnded_DoesNothing_AndThrowsNothing()
    {
        // A timer can still run its callback after Dispose (ITimer.Dispose does not wait for one
        // under way), and an exception on a timer thread ends the process. This clock's timers
        // ignore Dispose, so the callback does run on a finished deadline.
        var time = new UndisposableTimers(_time);
        var deadline = new RequestDeadline(time, Budget, CancellationToken.None, NullLogger.Instance);
        var token = deadline.Token;
        deadline.Dispose();

        var fire = () => _time.Advance(Budget);

        fire.Should().NotThrow();
        token.IsCancellationRequested.Should().BeFalse();
        deadline.FiredByDeadline.Should().BeFalse();
    }

    [Fact]
    public async Task TheResultFilter_SendsAResultThatExists_EvenAfterTheDeadlineFired()
    {
        // MVC's JSON formatter swallows a cancellation of RequestAborted and writes nothing, under
        // a status that already says success. The result is written under the client's own token.
        using var client = new CancellationTokenSource();
        using var deadline = Deadline(client.Token);
        _time.Advance(Budget);
        var http = new DefaultHttpContext { RequestAborted = deadline.Token };
        http.Features.Set<IRequestDeadline>(deadline);

        await RunResultFilterAsync(http);

        http.RequestAborted.Should().Be(client.Token);
        http.RequestAborted.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task TheResultFilter_KeepsTheRequestToken_OnceACommitStarted()
    {
        // The client hanging up after the commit must not cut the answer stored for replay: the
        // request's token, which nothing can cancel any more, stays the one the result is written under.
        using var client = new CancellationTokenSource();
        using var deadline = Deadline(client.Token);
        deadline.TryEnterCommit().Should().BeTrue();
        deadline.Committed();
        var http = new DefaultHttpContext { RequestAborted = deadline.Token };
        http.Features.Set<IRequestDeadline>(deadline);

        await RunResultFilterAsync(http);
        client.Cancel();
        _time.Advance(Budget);

        http.RequestAborted.Should().Be(deadline.Token);
        http.RequestAborted.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void TheResultFilter_IsRegistered_ForEveryAction()
    {
        // The two tests above prove what the filter does; this proves the host runs it. Nothing else
        // in the suite would notice it gone: once a commit has started the gate alone keeps the
        // request's token from being cancelled, so what is lost is a result racing the deadline,
        // which the formatter would send as an empty body under a status that says success.
        using var factory = new CustomWebApplicationFactory();

        var filters = factory.Services.GetRequiredService<IOptions<MvcOptions>>().Value.Filters;

        filters.OfType<TypeFilterAttribute>().Should().ContainSingle(
            f => f.ImplementationType == typeof(DeadlineResultFilter),
            "a global filter runs before every MVC result, which is where the deadline must be turned off");
    }

    [Fact]
    public async Task TheUserManager_TakesTheDeadlineItsScopeHolds_AndNoneWithoutOne()
    {
        // Identity's own UserManager runs every call with CancellationToken.None. The API's reads
        // the deadline from its scope at each call; only the middleware sets it, on the request's
        // scope, so a manager from any other scope runs with none.
        using var factory = new CustomWebApplicationFactory();
        using var deadline = Deadline();

        using var request = factory.Services.CreateScope();
        var inRequest = request.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        TokenOf(inRequest).Should().Be(CancellationToken.None, "the middleware has not set a deadline yet");

        request.ServiceProvider.GetRequiredService<RequestDeadlineScope>().Deadline = deadline;
        TokenOf(inRequest).Should().Be(deadline.Token, "read at each call, not when the manager was built");

        using var fresh = factory.Services.CreateScope();
        var elsewhere = fresh.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        TokenOf(elsewhere).Should().Be(CancellationToken.None, "a fresh scope holds no deadline");

        _time.Advance(Budget);
        await inRequest.Invoking(m => m.FindByEmailAsync("nobody@example.com"))
            .Should().ThrowAsync<OperationCanceledException>("the deadline fired: the store is handed its token");
        (await elsewhere.FindByEmailAsync("nobody@example.com")).Should().BeNull("and not handed to another scope's");
    }

    /// <summary>The token <paramref name="manager"/> hands its store, a protected property.</summary>
    private static CancellationToken TokenOf(UserManager<ApplicationUser> manager) =>
        (CancellationToken)typeof(UserManager<ApplicationUser>)
            .GetProperty("CancellationToken", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

    private static Task RunResultFilterAsync(HttpContext http) =>
        new DeadlineResultFilter().OnResultExecutionAsync(
            new ResultExecutingContext(
                new ActionContext(http, new RouteData(), new ActionDescriptor()), [], new OkResult(), new object()),
            () => Task.FromResult<ResultExecutedContext>(null!));

    [Fact]
    public async Task TheMiddleware_RunsTheRequestUnderItsToken_AndPutsTheClientsBack()
    {
        using var client = new CancellationTokenSource();
        var context = Context(client.Token, exempt: false);
        CancellationToken seen = default;
        IRequestDeadline? feature = null;
        var middleware = Middleware(next: c =>
        {
            seen = c.RequestAborted;
            feature = c.Features.Get<IRequestDeadline>();
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        feature.Should().NotBeNull();
        seen.Should().Be(feature!.Token).And.NotBe(client.Token);
        context.RequestServices.GetRequiredService<RequestDeadlineScope>().Deadline.Should().BeSameAs(feature,
            "the commit gate finds the deadline through the request's own scope");
        context.RequestAborted.Should().Be(client.Token, "the handlers above read the client's own token");
        context.Features.Get<IRequestDeadline>().Should().BeSameAs(feature, "and what the deadline recorded");
    }

    [Fact]
    public async Task AnEndpointMarkedNoRequestDeadline_RunsWithoutOne()
    {
        using var client = new CancellationTokenSource();
        var context = Context(client.Token, exempt: true);
        CancellationToken seen = default;

        await Middleware(next: c =>
        {
            seen = c.RequestAborted;
            return Task.CompletedTask;
        }).InvokeAsync(context);

        seen.Should().Be(client.Token);
        context.Features.Get<IRequestDeadline>().Should().BeNull();
        context.RequestServices.GetRequiredService<RequestDeadlineScope>().Deadline.Should().BeNull(
            "its commits are not gated");
    }

    private RequestDeadlineMiddleware Middleware(RequestDelegate next) =>
        new(next, _time, Microsoft.Extensions.Options.Options.Create(new RequestDeadlineOptions()),
            NullLogger<RequestDeadlineMiddleware>.Instance);

    private static DefaultHttpContext Context(CancellationToken clientAborted, bool exempt)
    {
        var services = new ServiceCollection().AddScoped<RequestDeadlineScope>().BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services.CreateScope().ServiceProvider,
            RequestAborted = clientAborted,
        };
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            exempt ? new EndpointMetadataCollection(new NoRequestDeadlineAttribute()) : EndpointMetadataCollection.Empty,
            "test"));
        return context;
    }

    /// <summary>A clock whose timers keep firing after they are disposed.</summary>
    private sealed class UndisposableTimers(TimeProvider inner) : TimeProvider
    {
        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new Kept(inner.CreateTimer(callback, state, dueTime, period));

        private sealed class Kept(ITimer timer) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
