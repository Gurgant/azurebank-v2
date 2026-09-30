using AzureBank.Api.Data;
using AzureBank.Api.Middleware;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AzureBank.Tests.Unit.Data;

/// <summary>
/// The commit gate (ADR-0058), called as EF calls it, on a context built from a request scope that
/// holds a deadline on a clock the test moves.
/// </summary>
/// <remarks>
/// <para>
/// The refusal is the one line that keeps a request the deadline has cancelled from committing: a
/// commit that reaches the gate between the deadline deciding to fire and its token being cancelled
/// finds the token not yet cancelled, and the synchronous commit takes no token at all. Neither
/// SqlClient nor EF would stop either, so the refusal is asserted here directly.
/// </para>
/// <para>
/// The context is never opened: the gate reads only the scope EF was given when it built the
/// context's options, which is how it finds the request's deadline.
/// </para>
/// <para>
/// IN THE SQL SERVER PROOFS' COLLECTION, so never beside them. A commit that lands here adds to
/// <c>ApiMetrics.CommitGateEntered</c>, a static counter, and <c>RequestDeadlineSqlServerTests</c>
/// counts that instrument across the whole process while a money request runs: one landing here in
/// parallel would read as a second commit of that request.
/// </para>
/// </remarks>
[Collection(SqlServerProofsCollection.Name)]
public sealed class CommitGateInterceptorTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private readonly FakeTimeProvider _time = new();
    private readonly CommitGateInterceptor _gate = new();
    private readonly List<IDisposable> _owned = [];

    private RequestDeadline Deadline()
    {
        var deadline = new RequestDeadline(_time, Budget, CancellationToken.None, NullLogger.Instance);
        _owned.Add(deadline);
        return deadline;
    }

    /// <summary>
    /// A context resolved in a scope whose deadline holder carries <paramref name="deadline"/>: what
    /// the middleware leaves on a request's scope, or null for a fresh scope. Without the holder
    /// registered at all, a host that has no gate.
    /// </summary>
    private DbContext Context(IRequestDeadline? deadline, bool holderRegistered = true)
    {
        var services = new ServiceCollection();
        if (holderRegistered)
        {
            services.AddScoped<RequestDeadlineScope>();
        }

        var root = services.BuildServiceProvider();
        var scope = root.CreateScope();
        if (holderRegistered)
        {
            scope.ServiceProvider.GetRequiredService<RequestDeadlineScope>().Deadline = deadline;
        }

        var context = new DbContext(new DbContextOptionsBuilder()
            .UseSqlServer("Server=.;Database=CommitGateInterceptorTests")
            .UseApplicationServiceProvider(scope.ServiceProvider)
            .Options);
        _owned.Add(context);
        _owned.Add(scope);
        _owned.Add(root);
        return context;
    }

    private static TransactionEventData Starting(DbContext context) =>
        new(null!, null!, null!, context, Guid.NewGuid(), Guid.NewGuid(), true, DateTimeOffset.UtcNow);

    private static TransactionEndEventData Ended(DbContext context) =>
        new(null!, null!, null!, context, Guid.NewGuid(), Guid.NewGuid(), true, DateTimeOffset.UtcNow, TimeSpan.Zero);

    private static TransactionErrorEventData FailedAt(DbContext context, string action) =>
        new(null!, null!, null!, context, Guid.NewGuid(), Guid.NewGuid(), true, action,
            new TimeoutException("A commit that failed (test)."), DateTimeOffset.UtcNow, TimeSpan.Zero);

    [Fact]
    public async Task ACommitAfterTheDeadlineFired_IsRefused_BeforeItStarts()
    {
        var deadline = Deadline();
        var context = Context(deadline);
        _time.Advance(Budget);

        var commit = async () => await _gate.TransactionCommittingAsync(
            null!, Starting(context), default, CancellationToken.None);

        (await commit.Should().ThrowAsync<OperationCanceledException>(
                "a request the deadline has cancelled commits nothing afterwards"))
            .Which.CancellationToken.Should().Be(deadline.Token);
        deadline.CommitEntered.Should().BeFalse("so its answer may still say nothing was applied");
    }

    [Fact]
    public void ACommitAfterTheDeadlineFired_IsRefused_OnTheSynchronousPath()
    {
        // DbTransaction.Commit takes no token: the gate is all that stands in its way.
        var deadline = Deadline();
        var context = Context(deadline);
        _time.Advance(Budget);

        var commit = () => _gate.TransactionCommitting(null!, Starting(context), default);

        commit.Should().Throw<OperationCanceledException>();
        deadline.CommitEntered.Should().BeFalse();
    }

    [Fact]
    public async Task ACommitBeforeTheDeadline_Starts_AndTheDeadlineCancelsNothingWhileItRuns()
    {
        var deadline = Deadline();
        var context = Context(deadline);

        var result = await _gate.TransactionCommittingAsync(null!, Starting(context), default, CancellationToken.None);
        _time.Advance(Budget + TimeSpan.FromSeconds(10));

        result.IsSuppressed.Should().BeFalse("the gate lets the commit start; it never skips it");
        deadline.CommitEntered.Should().BeTrue();
        deadline.Token.IsCancellationRequested.Should().BeFalse("a commit that started is never cancelled");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACommitThatFails_TurnsTheDeadlineBackOn_AtItsOriginalInstant(bool isAsync)
    {
        // Otherwise the execution strategy's retry of the whole operation runs with nothing able to
        // cancel it, bounded only by EF's own retry budget.
        var deadline = Deadline();
        var context = Context(deadline);
        await _gate.TransactionCommittingAsync(null!, Starting(context), default, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(30));

        if (isAsync)
        {
            await _gate.TransactionFailedAsync(null!, FailedAt(context, "Commit"), CancellationToken.None);
        }
        else
        {
            _gate.TransactionFailed(null!, FailedAt(context, "Commit"));
        }

        _time.Advance(TimeSpan.FromSeconds(9));
        deadline.Token.IsCancellationRequested.Should().BeFalse("39 s: the deadline is at 40");
        _time.Advance(TimeSpan.FromSeconds(1));
        deadline.Token.IsCancellationRequested.Should().BeTrue("the failed commit bought the request no time");
        deadline.FiredByDeadline.Should().BeTrue();
        deadline.CommitEntered.Should().BeTrue("a commit that failed may have landed");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACommitThatLanded_KeepsTheDeadlineOff_ForTheRestOfTheRequest(bool isAsync)
    {
        var deadline = Deadline();
        var context = Context(deadline);
        await _gate.TransactionCommittingAsync(null!, Starting(context), default, CancellationToken.None);

        if (isAsync)
        {
            await _gate.TransactionCommittedAsync(null!, Ended(context), CancellationToken.None);
        }
        else
        {
            _gate.TransactionCommitted(null!, Ended(context));
        }

        // A later commit on the same request that fails does not bring back a deadline the request
        // is already past: what committed is answered.
        await _gate.TransactionCommittingAsync(null!, Starting(context), default, CancellationToken.None);
        await _gate.TransactionFailedAsync(null!, FailedAt(context, "Commit"), CancellationToken.None);
        _time.Advance(Budget + TimeSpan.FromSeconds(10));

        deadline.Token.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task AContextFromAFreshScope_IsNeverGated_WhileTheRequestsOwnIs()
    {
        // A refusal's audit row and the release of an idempotency claim are written in a fresh
        // scope, after the request's deadline has fired, and must land.
        var deadline = Deadline();
        var request = Context(deadline);
        var fresh = Context(deadline: null);
        var otherHost = Context(deadline: null, holderRegistered: false);
        _time.Advance(Budget);

        var requestCommit = async () => await _gate.TransactionCommittingAsync(
            null!, Starting(request), default, CancellationToken.None);
        var freshCommit = async () => await _gate.TransactionCommittingAsync(
            null!, Starting(fresh), default, CancellationToken.None);
        var otherHostCommit = () => _gate.TransactionCommitting(null!, Starting(otherHost), default);

        await requestCommit.Should().ThrowAsync<OperationCanceledException>();
        await freshCommit.Should().NotThrowAsync();
        otherHostCommit.Should().NotThrow();
    }

    [Fact]
    public async Task ASavepointRollback_IsSkipped_OnlyWhenTheRequestItselfWasCancelled()
    {
        var fired = Deadline();
        var firedContext = Context(fired);
        _time.Advance(Budget);
        var armed = Deadline();
        var armedContext = Context(armed);
        using var otherToken = new CancellationTokenSource();
        await otherToken.CancelAsync();

        (await _gate.RollingBackToSavepointAsync(null!, Starting(firedContext), default, fired.Token))
            .IsSuppressed.Should().BeTrue(
                "the request is cancelled, so no commit can start and the rows can never land");
        (await _gate.RollingBackToSavepointAsync(null!, Starting(armedContext), default, otherToken.Token))
            .IsSuppressed.Should().BeFalse(
                "only the save's token was cancelled: the request may still commit, and the "
                + "savepoint is what keeps the failed save's rows out of that commit");
        (await _gate.RollingBackToSavepointAsync(null!, Starting(firedContext), default, CancellationToken.None))
            .IsSuppressed.Should().BeFalse("a rollback that can run is left to run");
        (await _gate.RollingBackToSavepointAsync(null!, Starting(Context(deadline: null)), default, otherToken.Token))
            .IsSuppressed.Should().BeFalse("a fresh scope's context is never touched");
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            _owned[i].Dispose();
        }
    }
}
