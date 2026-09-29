using System.Reflection;
using System.Text.Json;
using AzureBank.Api.Handlers;
using AzureBank.Api.Middleware;
using AzureBank.Shared.Constants;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AzureBank.Tests.Unit.Handlers;

/// <summary>
/// Which failures answer the outage 503 and which stay a 500 (ADR-0058), and what the two
/// handlers write: <see cref="ServiceUnavailableExceptionHandler"/> and
/// <see cref="ClientAbortedExceptionHandler"/>.
/// </summary>
/// <remarks>
/// The SQL errors are built the way SqlClient builds them (its internal factory, through
/// reflection): the handler reads their numbers and classes, and no public constructor exists.
/// The SQL Server proofs make the real ones (<c>DatabaseUnavailableSqlServerTests</c>).
/// </remarks>
public class ServiceUnavailableExceptionHandlerTests
{
    [Theory]
    [InlineData(4060, 11)] // cannot open database: EF's transient list
    [InlineData(40613, 17)] // Azure: database not currently available
    [InlineData(11001, 20)] // host not found (a stopped container, locally)
    [InlineData(1205, 13)] // deadlock victim, when it reaches the handler unretried
    [InlineData(35, 20)] // no route to the server
    [InlineData(18401, 14)] // server in script-upgrade mode
    [InlineData(17197, 20)] // login timed out
    [InlineData(17142, 14)] // server paused
    [InlineData(258, 20)] // a refused connection on Windows, measured: a wait that timed out, class 20
    public void AnUnreachableDatabase_IsTheOutage503(int number, byte errorClass)
    {
        ServiceUnavailableExceptionHandler.ReasonFor(Sql(number, errorClass), firedByDeadline: false)
            .Should().Be(ServiceUnavailableExceptionHandler.DatabaseUnreachable);
    }

    [Fact]
    public void ACommandTimeout_IsTheOutage503()
    {
        ServiceUnavailableExceptionHandler.ReasonFor(Sql(-2, 11), firedByDeadline: false)
            .Should().Be(ServiceUnavailableExceptionHandler.CommandTimeout);
    }

    [Theory]
    [InlineData(2627, 14)] // a primary key or unique constraint
    [InlineData(2601, 14)] // a unique index
    [InlineData(2628, 16)] // truncation
    [InlineData(50000, 16)] // the application lock's own THROW: a raised error, not an outage
    [InlineData(547, 16)] // a foreign key
    public void AnythingElse_StaysA500(int number, byte errorClass)
    {
        ServiceUnavailableExceptionHandler.ReasonFor(Sql(number, errorClass), firedByDeadline: false)
            .Should().BeNull("only an outage is a 503; a defect must still read as one");
        ServiceUnavailableExceptionHandler.ReasonFor(new DbUpdateConcurrencyException("stale"), firedByDeadline: false)
            .Should().BeNull();
        ServiceUnavailableExceptionHandler.ReasonFor(new InvalidOperationException("a bug"), firedByDeadline: false)
            .Should().BeNull();
    }

    [Fact]
    public void TheChainIsWalked_ToWhatFailedUnderneath()
    {
        // A save wraps its SqlException; EF's non-retrying strategy wraps a transient in an
        // InvalidOperationException; a retrying one ends in RetryLimitExceededException.
        ServiceUnavailableExceptionHandler.ReasonFor(new DbUpdateException("save", Sql(4060, 11)), false)
            .Should().Be(ServiceUnavailableExceptionHandler.DatabaseUnreachable);
        ServiceUnavailableExceptionHandler.ReasonFor(
                new InvalidOperationException("likely due to a transient failure", new TimeoutException()), false)
            .Should().Be(ServiceUnavailableExceptionHandler.Timeout);
        ServiceUnavailableExceptionHandler.ReasonFor(new RetryLimitExceededException("spent", Sql(4060, 11)), false)
            .Should().Be(ServiceUnavailableExceptionHandler.RetryLimitExceeded);
        ServiceUnavailableExceptionHandler.ReasonFor(new DbUpdateException("save", Sql(2627, 14)), false)
            .Should().BeNull();
    }

    [Fact]
    public void AnExhaustedPool_IsTheOutage503_ByItsMessage()
    {
        // SqlClient's text, measured with a pool of one (DatabaseUnavailableSqlServerTests).
        var poolTimeout = new InvalidOperationException(
            "Timeout expired.  The timeout period elapsed prior to obtaining a connection from the pool.  "
            + "This may have occurred because all pooled connections were in use and max pool size was reached.");

        ServiceUnavailableExceptionHandler.ReasonFor(poolTimeout, firedByDeadline: false)
            .Should().Be(ServiceUnavailableExceptionHandler.PoolExhausted);
    }

    [Fact]
    public void ARequestTheDeadlineCancelled_IsTheOutage503_WhateverItThrew()
    {
        // A cancelled command surfaces as whatever SqlClient and EF make of it: number 0, a
        // DbUpdateException, an OperationCanceledException.
        ServiceUnavailableExceptionHandler.ReasonFor(new DbUpdateException("save", Sql(0, 11)), firedByDeadline: true)
            .Should().Be(ServiceUnavailableExceptionHandler.Deadline);
        ServiceUnavailableExceptionHandler.ReasonFor(new OperationCanceledException(), firedByDeadline: true)
            .Should().Be(ServiceUnavailableExceptionHandler.Deadline);
    }

    [Fact]
    public void TheLogNamesEverySqlErrorAndEveryTypeInTheChain()
    {
        var exception = new RetryLimitExceededException("spent", Sql(4060, 11, 18456));

        ServiceUnavailableExceptionHandler.SqlErrorNumbers(exception).Should().Equal(4060, 18456);
        ServiceUnavailableExceptionHandler.ExceptionTypes(exception)
            .Should().Be("RetryLimitExceededException > SqlException");
    }

    [Fact]
    public async Task TheOutage503_HasItsWholeShape()
    {
        var context = NewContext();

        var handled = await Handler().TryHandleAsync(context, Sql(4060, 11), CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers.RetryAfter.ToString().Should().Be("10");
        context.Response.Headers.CacheControl.ToString().Should().Be("no-store");

        var body = Body(context);
        body.GetProperty("status").GetInt32().Should().Be(503);
        body.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.ServiceUnavailable);
        body.GetProperty("retryAfterSeconds").GetInt32().Should().Be(10);
        body.TryGetProperty("traceId", out _).Should().BeTrue();
        body.TryGetProperty("applied", out _).Should().BeFalse("nothing here knows what was applied");
    }

    [Fact]
    public async Task ADefect_IsLeftToTheGlobalHandler()
    {
        var context = NewContext();

        (await Handler().TryHandleAsync(context, Sql(2627, 14), CancellationToken.None)).Should().BeFalse();
        context.Response.Body.Length.Should().Be(0);
    }

    [Fact]
    public async Task ARequestTheDeadlineCancelled_IsAnswered503_EvenForAnErrorThatIsNotAnOutage()
    {
        var context = NewContext();
        var time = new FakeTimeProvider();
        using var deadline = new RequestDeadline(time, TimeSpan.FromSeconds(1), CancellationToken.None, NullLogger.Instance);
        context.Features.Set<IRequestDeadline>(deadline);
        time.Advance(TimeSpan.FromSeconds(1));

        // 2627 would be a 500 on its own: here it is what a cancelled save happened to raise.
        (await Handler().TryHandleAsync(context, new DbUpdateException("save", Sql(2627, 14)), CancellationToken.None))
            .Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task AClientThatHungUp_GetsNothing()
    {
        using var hungUp = new CancellationTokenSource();
        hungUp.Cancel();
        var context = NewContext();
        context.RequestAborted = hungUp.Token;

        var handled = await new ClientAbortedExceptionHandler(NullLogger<ClientAbortedExceptionHandler>.Instance)
            .TryHandleAsync(context, Sql(0, 11), CancellationToken.None);

        handled.Should().BeTrue("nothing below may write to a client that has gone, or log it as an error");
        context.Response.StatusCode.Should().Be(StatusCodes.Status499ClientClosedRequest);
        context.Response.Body.Length.Should().Be(0);
    }

    [Fact]
    public async Task AClientStillThere_IsLeftToTheOtherHandlers()
    {
        var context = NewContext();

        (await new ClientAbortedExceptionHandler(NullLogger<ClientAbortedExceptionHandler>.Instance)
            .TryHandleAsync(context, Sql(4060, 11), CancellationToken.None)).Should().BeFalse();
    }

    private static ServiceUnavailableExceptionHandler Handler() =>
        new(NullLogger<ServiceUnavailableExceptionHandler>.Instance, ReceivedAtClock.ForThisProcess);

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/accounts";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static JsonElement Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return JsonDocument.Parse(context.Response.Body).RootElement.Clone();
    }

    /// <summary>A <see cref="SqlException"/> carrying one error per number, all of one class.</summary>
    private static SqlException Sql(int number, byte errorClass, params int[] more)
    {
        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        var add = typeof(SqlErrorCollection).GetMethod("Add", Any)!;
        var ctor = typeof(SqlError).GetConstructor(
            Any, [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception)])!;

        foreach (var n in more.Prepend(number))
        {
            add.Invoke(collection, [ctor.Invoke([n, (byte)0, errorClass, "server", $"error {n}", "", 0, null])]);
        }

        var create = typeof(SqlException).GetMethod(
            "CreateException", Any, [typeof(SqlErrorCollection), typeof(string)])!;
        return (SqlException)create.Invoke(null, [collection, "16.0"])!;
    }
}
