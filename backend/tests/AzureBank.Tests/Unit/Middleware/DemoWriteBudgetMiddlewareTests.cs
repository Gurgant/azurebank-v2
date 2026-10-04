using System.Data.Common;
using System.Security.Claims;
using AzureBank.Api.Attributes;
using AzureBank.Api.Middleware;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureBank.Tests.Unit.Middleware;

/// <summary>
/// The middleware that bounds what one demo copy can change: with <c>Demo:Enabled</c> true, every
/// request of a signed-in user that changes something is counted on the caller's copy, and the one
/// past <c>Demo:Copy:MaxWrites</c> is refused before anything behind the middleware runs.
/// </summary>
/// <remarks>
/// <para>
/// On a bare <see cref="DefaultHttpContext"/>, so each condition is shown alone: the flag, the
/// method, the two markers, the caller, and the copy the caller belongs to.
/// </para>
/// <para>
/// Two kinds of context. What must send nothing runs on a SQL Server context that is never opened
/// and whose interceptor fails the first attempt to open it (<see cref="NoStatementLeaves"/>): a
/// request that passed there asked the database nothing. What is counted runs on the InMemory
/// provider, where the middleware takes its other branch (read, check, add, save). The statement
/// itself, and what it does when several arrive at once, is shown on SQL Server
/// (<c>DemoWriteBudgetSqlServerTests</c>).
/// </para>
/// </remarks>
public sealed class DemoWriteBudgetMiddlewareTests
{
    private static Endpoint EndpointWith(params object[] metadata) =>
        new(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "an endpoint");

    private static Endpoint? EndpointOf(string kind) => kind switch
    {
        "plain" => EndpointWith(),
        "marked" => EndpointWith(new CountedAsDemoWriteAttribute()),
        "token" => EndpointWith(new TokenEndpointAttribute()),
        "none" => null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no such kind of endpoint in these tests"),
    };

    /// <summary>A signed-in caller whose id is in <paramref name="claimType"/>.</summary>
    private static ClaimsPrincipal Caller(Guid userId, string claimType = ClaimTypes.NameIdentifier) =>
        new(new ClaimsIdentity([new Claim(claimType, userId.ToString())], authenticationType: "Bearer"));

    /// <summary>Nobody: the principal a request carries when no token was presented.</summary>
    private static ClaimsPrincipal Nobody() => new(new ClaimsIdentity());

    private static DemoOptions Demo(bool on, int maxWrites = 10) =>
        new() { Enabled = on, Copy = new DemoCopyOptions { MaxWrites = maxWrites } };

    /// <summary>Runs one request through the middleware: whether the next one ran, and what was thrown.</summary>
    private static async Task<(bool NextRan, Exception? Thrown)> SendAsync(
        AzureBankDbContext db, DemoOptions demo, string method, Endpoint? endpoint, ClaimsPrincipal user,
        CancellationToken requestAborted = default)
    {
        var nextRan = false;
        var middleware = new DemoWriteBudgetMiddleware(
            _ =>
            {
                nextRan = true;
                return Task.CompletedTask;
            },
            Options.Create(demo));

        var context = new DefaultHttpContext { User = user, RequestAborted = requestAborted };
        context.Request.Method = method;
        context.SetEndpoint(endpoint);

        var thrown = await Record.ExceptionAsync(() => middleware.InvokeAsync(context, db));
        return (nextRan, thrown);
    }

    // ── What sends nothing ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fails the first step of any statement, the opening of the connection, and counts the
    /// attempts: a context that carries it cannot send anything unnoticed.
    /// </summary>
    private sealed class NoStatementLeaves : DbConnectionInterceptor
    {
        public const string Message = "TEST: a statement was on its way to the database.";

        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public override InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException(Message);
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException(Message);
        }
    }

    /// <summary>A SQL Server context on a database nobody opens: relational, and watched by <paramref name="watch"/>.</summary>
    private static AzureBankDbContext NeverOpened(NoStatementLeaves watch) =>
        new(new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseSqlServer(CustomWebApplicationFactory.PlaceholderConnectionString)
            .AddInterceptors(watch)
            .Options);

    /// <summary>
    /// ARRANGE, and the control of every test that says "nothing was sent": on this context a
    /// request the budget counts does go to the database, and the watch is what stops it.
    /// </summary>
    private static async Task ACountedRequestIsStoppedByTheWatchAsync(AzureBankDbContext db, NoStatementLeaves watch)
    {
        var (nextRan, thrown) = await SendAsync(db, Demo(on: true), "POST", EndpointWith(), Caller(Guid.CreateVersion7()));

        using (new AssertionScope())
        {
            (thrown?.Message).Should().Be(
                NoStatementLeaves.Message, "CONTROL: a change by a signed-in user of the demo is counted, and counting asks the database");
            watch.Attempts.Should().Be(1, "CONTROL: the watch is on the context the middleware uses");
            nextRan.Should().BeFalse("CONTROL: a request whose count could not be written goes no further");
        }
    }

    public static TheoryData<string, bool, string, string, bool> WhatPasses() => new()
    {
        // what, the demo on, the method, the endpoint, signed in
        { "the demo is off", false, "POST", "plain", true },
        { "the demo is off, on the endpoint a safe method writes through", false, "GET", "marked", true },
        { "a GET", true, "GET", "plain", true },
        { "a HEAD", true, "HEAD", "plain", true },
        { "an OPTIONS", true, "OPTIONS", "plain", true },
        { "a TRACE", true, "TRACE", "plain", true },
        { "nobody is signed in", true, "POST", "plain", false },
        { "nobody is signed in, on the endpoint a safe method writes through", true, "GET", "marked", false },
        { "no endpoint was selected", true, "POST", "none", true },
        { "a token endpoint", true, "POST", "token", true },
    };

    [Theory]
    [MemberData(nameof(WhatPasses))]
    public async Task WithTheDemoOff_OrASafeMethod_OrNoUser_ItPasses_AndSendsNothing(
        string what, bool demoOn, string method, string endpoint, bool signedIn)
    {
        var watch = new NoStatementLeaves();
        await using var db = NeverOpened(watch);
        await ACountedRequestIsStoppedByTheWatchAsync(db, watch);

        var (nextRan, thrown) = await SendAsync(
            db, Demo(demoOn), method, EndpointOf(endpoint), signedIn ? Caller(Guid.CreateVersion7()) : Nobody());

        using (new AssertionScope())
        {
            thrown.Should().BeNull("{0}: the budget has nothing to say", what);
            nextRan.Should().BeTrue("{0}: the request goes on", what);
            watch.Attempts.Should().Be(1, "{0}: nothing was sent for it, and the one attempt is the control's", what);
        }
    }

    public static TheoryData<string, string?> UnusableIds() => new()
    {
        { "no id at all", null },
        { "an id that is not one", "not-an-id" },
    };

    [Theory]
    [MemberData(nameof(UnusableIds))]
    public async Task ASignedInCallerWithNoUsableId_IsRefusedAsAnInvalidToken_AndNothingIsSent(string what, string? id)
    {
        var watch = new NoStatementLeaves();
        await using var db = NeverOpened(watch);
        await ACountedRequestIsStoppedByTheWatchAsync(db, watch);
        var caller = new ClaimsPrincipal(new ClaimsIdentity(
            id is null ? [new Claim(ClaimTypes.Email, "someone@example.com")] : [new Claim(ClaimTypes.NameIdentifier, id)],
            authenticationType: "Bearer"));

        var (nextRan, thrown) = await SendAsync(db, Demo(on: true), "POST", EndpointWith(), caller);

        using (new AssertionScope())
        {
            // The answer the idempotency middleware gives the same caller: a token that names
            // nobody proves nothing.
            thrown.Should().BeOfType<AuthenticationException>("{0}: no copy can be found for a caller nobody can name", what);
            ((thrown as AppException)?.ErrorCode).Should().Be(ErrorCodes.TokenInvalid);
            nextRan.Should().BeFalse();
            watch.Attempts.Should().Be(1, "nothing was sent for it, and the one attempt is the control's");
        }
    }

    // ── What is counted ──────────────────────────────────────────────────────────────────────────

    /// <summary>An InMemory database of one test's own, and what a test puts in it and reads from it.</summary>
    private sealed class Store
    {
        private readonly DbContextOptions<AzureBankDbContext> _options =
            new DbContextOptionsBuilder<AzureBankDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        private int _users;

        /// <summary>A context of its own for each request, as each request has a scope of its own.</summary>
        public AzureBankDbContext NewContext() => new(_options);

        /// <summary>A copy a visitor holds, with its owner and one contact.</summary>
        public async Task<(Guid Id, Guid OwnerId, Guid ContactId)> AddCopyAsync(int writes = 0, DateTime? deletedAt = null)
        {
            var copy = new DemoCopy
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = Guid.CreateVersion7(),
                CreatedAt = DateTime.UtcNow.AddHours(-2),
                ClaimedAt = DateTime.UtcNow.AddHours(-1),
                ClaimId = Guid.CreateVersion7(),
                Writes = writes,
                DeletedAt = deletedAt,
            };
            var contactId = Guid.CreateVersion7();

            await using var db = NewContext();
            db.DemoCopies.Add(copy);
            db.Users.Add(User(copy.OwnerUserId, copy.Id));
            db.Users.Add(User(contactId, copy.Id));
            await db.SaveChangesAsync();
            return (copy.Id, copy.OwnerUserId, contactId);
        }

        /// <summary>A user who belongs to no copy.</summary>
        public async Task<Guid> AddUserOutsideEveryCopyAsync()
        {
            var id = Guid.CreateVersion7();
            await using var db = NewContext();
            db.Users.Add(User(id, copyId: null));
            await db.SaveChangesAsync();
            return id;
        }

        public async Task<int> WritesOfAsync(Guid copyId)
        {
            await using var db = NewContext();
            return await db.DemoCopies.AsNoTracking().Where(c => c.Id == copyId).Select(c => c.Writes).SingleAsync();
        }

        private ApplicationUser User(Guid id, Guid? copyId) => new()
        {
            Id = id,
            UserName = id.ToString(),
            AzureTag = $"user_{Interlocked.Increment(ref _users):D4}",
            FirstName = "Demo",
            LastName = "User",
            DemoCopyId = copyId,
        };
    }

    /// <summary>One request on a context of its own.</summary>
    private static async Task<(bool NextRan, Exception? Thrown)> SendAsync(
        Store store, DemoOptions demo, string method, Endpoint? endpoint, ClaimsPrincipal user)
    {
        await using var db = store.NewContext();
        return await SendAsync(db, demo, method, endpoint, user);
    }

    private static void ShouldBeTheCopyLimit(Exception? thrown)
    {
        thrown.Should().BeOfType<DemoRefusalException>();
        ((thrown as AppException)?.StatusCode).Should().Be(StatusCodes.Status429TooManyRequests);
        ((thrown as AppException)?.ErrorCode).Should().Be(ErrorCodes.DemoCopyLimit);
        (thrown?.Message).Should().Be(DemoRefusalException.CopyLimitDetail);
        ((thrown as AppException)?.Details).Should().BeNullOrEmpty("this refusal ends at no instant anybody can compute, so it names no wait");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(13)]
    public async Task OnAProviderThatIsNotRelational_ItCountsAndRefuses(int limit)
    {
        var store = new Store();
        var copy = await store.AddCopyAsync();
        var demo = Demo(on: true, maxWrites: limit);

        for (var change = 1; change <= limit; change++)
        {
            var (ran, refused) = await SendAsync(store, demo, "POST", EndpointWith(), Caller(copy.OwnerId));

            refused.Should().BeNull("change {0} of {1} is inside the copy's budget", change, limit);
            ran.Should().BeTrue("change {0} of {1} goes on to its endpoint", change, limit);
            (await store.WritesOfAsync(copy.Id)).Should().Be(change, "each change that passes is counted once");
        }

        var (nextRan, thrown) = await SendAsync(store, demo, "POST", EndpointWith(), Caller(copy.OwnerId));

        using (new AssertionScope())
        {
            ShouldBeTheCopyLimit(thrown);
            nextRan.Should().BeFalse("no binding, no validation, no action: nothing is written for a change past the limit");
            (await store.WritesOfAsync(copy.Id)).Should().Be(limit, "a refused change is not counted: the count stops at the limit");
        }

        // What changes nothing is still let through: a copy at its limit can be looked at.
        var (readRan, readRefused) = await SendAsync(store, demo, "GET", EndpointWith(), Caller(copy.OwnerId));
        using (new AssertionScope())
        {
            readRefused.Should().BeNull();
            readRan.Should().BeTrue();
            (await store.WritesOfAsync(copy.Id)).Should().Be(limit);
        }
    }

    public static TheoryData<string, string, string> WhatIsCounted() => new()
    {
        // what, the method, the endpoint
        { "a POST", "POST", "plain" },
        { "a PUT", "PUT", "plain" },
        { "a PATCH", "PATCH", "plain" },
        { "a DELETE", "DELETE", "plain" },
        { "a method that is not one of the four safe ones", "PROPFIND", "plain" },
        { "a GET on the endpoint a safe method writes through", "GET", "marked" },
    };

    [Theory]
    [MemberData(nameof(WhatIsCounted))]
    public async Task AChangeByASignedInUserOfACopy_IsCountedOnce_AndGoesOn(string what, string method, string endpoint)
    {
        var store = new Store();
        var copy = await store.AddCopyAsync(writes: 4);

        var (nextRan, thrown) = await SendAsync(store, Demo(on: true), method, EndpointOf(endpoint), Caller(copy.OwnerId));

        using (new AssertionScope())
        {
            thrown.Should().BeNull("{0} is inside the budget", what);
            nextRan.Should().BeTrue("{0} goes on to its endpoint", what);
            (await store.WritesOfAsync(copy.Id)).Should().Be(5, "{0} is counted once", what);
        }
    }

    [Theory]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("sub")]
    public async Task TheCallersId_IsReadFromEitherClaimATokenCarriesItIn(string claimType)
    {
        var store = new Store();
        var copy = await store.AddCopyAsync();

        var (nextRan, thrown) = await SendAsync(store, Demo(on: true), "POST", EndpointWith(), Caller(copy.OwnerId, claimType));

        using (new AssertionScope())
        {
            thrown.Should().BeNull();
            nextRan.Should().BeTrue();
            (await store.WritesOfAsync(copy.Id)).Should().Be(1);
        }
    }

    [Fact]
    public async Task EveryUserOfACopy_SpendsTheSameBudget()
    {
        var store = new Store();
        var copy = await store.AddCopyAsync();
        var demo = Demo(on: true);

        var owner = await SendAsync(store, demo, "POST", EndpointWith(), Caller(copy.OwnerId));
        var contact = await SendAsync(store, demo, "POST", EndpointWith(), Caller(copy.ContactId));

        using (new AssertionScope())
        {
            owner.NextRan.Should().BeTrue();
            contact.NextRan.Should().BeTrue();
            (await store.WritesOfAsync(copy.Id)).Should().Be(2, "the budget is the copy's, whichever of its users spends it");
        }
    }

    [Fact]
    public async Task ACopyAtItsLimit_StopsItsOwnUsers_AndNobodyElses()
    {
        var store = new Store();
        var full = await store.AddCopyAsync(writes: 10);
        var other = await store.AddCopyAsync(writes: 3);
        var demo = Demo(on: true, maxWrites: 10);

        var (otherRan, otherRefused) = await SendAsync(store, demo, "POST", EndpointWith(), Caller(other.OwnerId));

        using (new AssertionScope())
        {
            otherRefused.Should().BeNull("another copy's limit is not this copy's");
            otherRan.Should().BeTrue();
            (await store.WritesOfAsync(other.Id)).Should().Be(4, "the change is counted on the caller's own copy");
            (await store.WritesOfAsync(full.Id)).Should().Be(10, "and on no other");
        }

        var (fullRan, fullRefused) = await SendAsync(store, demo, "POST", EndpointWith(), Caller(full.OwnerId));

        using (new AssertionScope())
        {
            ShouldBeTheCopyLimit(fullRefused);
            fullRan.Should().BeFalse();
            (await store.WritesOfAsync(full.Id)).Should().Be(10);
            (await store.WritesOfAsync(other.Id)).Should().Be(4, "a refusal counts nothing anywhere");
        }
    }

    [Fact]
    public async Task ARequestAlreadyCancelled_IsStoppedAtTheCount_AndCountsNothing()
    {
        var store = new Store();
        var copy = await store.AddCopyAsync(writes: 4);
        await using var db = store.NewContext();

        var (nextRan, thrown) = await SendAsync(
            db, Demo(on: true), "POST", EndpointWith(), Caller(copy.OwnerId), requestAborted: new CancellationToken(canceled: true));

        using (new AssertionScope())
        {
            // The count runs under the request's own token, which its deadline and a caller
            // hanging up both cancel: a request that is over asks the database nothing more.
            thrown.Should().BeAssignableTo<OperationCanceledException>();
            nextRan.Should().BeFalse();
            (await store.WritesOfAsync(copy.Id)).Should().Be(4);
        }

        // CONTROL: the same request, not cancelled, is counted and goes on.
        var control = await SendAsync(store, Demo(on: true), "POST", EndpointWith(), Caller(copy.OwnerId));
        using (new AssertionScope())
        {
            control.NextRan.Should().BeTrue("CONTROL");
            (await store.WritesOfAsync(copy.Id)).Should().Be(5, "CONTROL");
        }
    }

    public static TheoryData<string> CallersWithNoBudget() => new()
    {
        "a user who belongs to no copy",
        "an id no user has",
        "a user whose copy is the record of a deleted one",
    };

    [Theory]
    [MemberData(nameof(CallersWithNoBudget))]
    public async Task ACallerWithNoCopyToCountOn_IsRefused_AndNothingIsCounted(string who)
    {
        var store = new Store();
        var bystander = await store.AddCopyAsync(writes: 3);
        var deleted = await store.AddCopyAsync(writes: 2, deletedAt: DateTime.UtcNow);
        var caller = who switch
        {
            "a user who belongs to no copy" => await store.AddUserOutsideEveryCopyAsync(),
            "an id no user has" => Guid.CreateVersion7(),
            _ => deleted.OwnerId,
        };

        var (nextRan, thrown) = await SendAsync(store, Demo(on: true), "POST", EndpointWith(), Caller(caller));

        using (new AssertionScope())
        {
            // On the demo only a copy's owner can sign in, so such a caller has no session to send
            // this with. If one ever does, it changes nothing: there is no budget to spend.
            ShouldBeTheCopyLimit(thrown);
            nextRan.Should().BeFalse("{0}: no copy, no budget", who);
            (await store.WritesOfAsync(bystander.Id)).Should().Be(3, "nobody else's copy is counted on");
            (await store.WritesOfAsync(deleted.Id)).Should().Be(2, "a record is not counted on");
        }

        // CONTROL: on the same store a user of a living copy is let through and counted.
        var control = await SendAsync(store, Demo(on: true), "POST", EndpointWith(), Caller(bystander.OwnerId));
        using (new AssertionScope())
        {
            control.NextRan.Should().BeTrue("CONTROL");
            (await store.WritesOfAsync(bystander.Id)).Should().Be(4, "CONTROL");
        }
    }
}
