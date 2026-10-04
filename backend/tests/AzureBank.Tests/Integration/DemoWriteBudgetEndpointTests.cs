using System.Net;
using System.Text.Json.Nodes;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The budget of one demo copy, through the host: where it sits in the pipeline, what its refusal
/// looks like once the exception handler has written it, and which requests it counts.
/// </summary>
/// <remarks>
/// <para>
/// Two hosts of its own: a default one, where the demo is off, and one with the demo on and the
/// limit at 10, the lowest a host starts with.
/// </para>
/// <para>
/// On the InMemory provider, where the middleware reads, checks, adds and saves in place of its one
/// statement: what is shown here is the pipeline and the answers. That the count holds when
/// requests arrive together is shown on SQL Server (<c>DemoWriteBudgetSqlServerTests</c>). A copy
/// here is made by hand (<see cref="HandMadeDemoCopy"/>): three users and no account, so the
/// changes it makes are PIN verifications, the account it asks about does not exist, and the
/// deposit it sends carries no idempotency key and is refused for that.
/// </para>
/// </remarks>
public sealed class DemoWriteBudgetEndpointTests : IDisposable
{
    private const int Limit = 10;

    /// <summary>The PIN every user of a copy is seeded with.</summary>
    private const string DemoPin = "123456";

    private readonly CustomWebApplicationFactory _ordinary = new();
    private readonly CustomWebApplicationFactory _demo = new();

    public DemoWriteBudgetEndpointTests() => _demo.EnableDemo(("Demo:Copy:MaxWrites", "10"));

    public void Dispose()
    {
        _ordinary.Dispose();
        _demo.Dispose();
    }

    /// <summary>Makes the demo host's one free copy and claims it: the copy, and its owner on the session the claim opened.</summary>
    private async Task<(HandMadeDemoCopy Copy, DemoVisitor Owner)> ClaimACopyAsync(HttpClient client)
    {
        var copy = await HandMadeDemoCopy.CreateAsync(_demo);
        using var response = await DemoVisitor.ClaimAsync(client);
        var claim = await DemoVisitor.ClaimedAsync(response);
        claim.User.Id.Should().Be(copy.Owner.Id, "ARRANGE: the one free copy is the one that was claimed");
        return (copy, DemoVisitor.OfClaim(client, claim));
    }

    private static async Task<int> WritesOfAsync(CustomWebApplicationFactory api, HandMadeDemoCopy copy) =>
        (await copy.RowAsync(api)).Writes;

    /// <summary>ARRANGE: a copy that has already made <paramref name="writes"/> changes.</summary>
    private static async Task SetWritesAsync(CustomWebApplicationFactory api, HandMadeDemoCopy copy, int writes)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var row = await db.DemoCopies.SingleAsync(c => c.Id == copy.Id);
        row.Writes = writes;
        await db.SaveChangesAsync();
    }

    private static async Task<JsonObject> BodyOfAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    private static string[] MembersOf(JsonObject body) => [.. body.Select(member => member.Key).Order(StringComparer.Ordinal)];

    // ── The limit ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithTheDemoOn_TheChangePastACopysLimit_Is429CopyLimit_AndAReadAndSigningOutEverywhereStillAnswer()
    {
        using var client = _demo.CreateClient();
        var (copy, owner) = await ClaimACopyAsync(client);
        (await WritesOfAsync(_demo, copy)).Should().Be(0, "the claim is not a change of the copy's");

        for (var change = 1; change <= Limit; change++)
        {
            using var verified = await owner.VerifyPinAsync(DemoPin);

            verified.StatusCode.Should().Be(HttpStatusCode.OK, "change {0} of {1} is inside the budget", change, Limit);
            (await WritesOfAsync(_demo, copy)).Should().Be(change, "each change is counted once");
        }

        using var refused = await owner.VerifyPinAsync(DemoPin);

        // CONTROL: another refusal of the application's, for the shape a refusal has.
        using var wrongSignIn = await DemoVisitor.TrySignInAsync(client, copy.Owner.Email!, "Wrong-Pass-2026!");
        wrongSignIn.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "ARRANGE");

        var body = await BodyOfAsync(refused);
        using (new AssertionScope())
        {
            refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            (body["errorCode"]?.GetValue<string>()).Should().Be(ErrorCodes.DemoCopyLimit);
            (body["detail"]?.GetValue<string>()).Should().Be(DemoRefusalException.CopyLimitDetail);
            (body["status"]?.GetValue<int>()).Should().Be(429);
            (body["title"]?.GetValue<string>()).Should().Be("Too Many Requests");
            (body["instance"]?.GetValue<string>()).Should().Be("/api/auth/pin/verify");
            (body["traceId"]?.GetValue<string>()).Should().NotBeNullOrWhiteSpace();

            // Written by the exception handler, as every refusal with a code is: the same members
            // and the same media type as a wrong sign-in's 401, and nothing that names a wait.
            MembersOf(body).Should().Equal(MembersOf(await BodyOfAsync(wrongSignIn)));
            (refused.Content.Headers.ContentType?.MediaType).Should().Be(wrongSignIn.Content.Headers.ContentType?.MediaType);
            body.ContainsKey("retryAfterSeconds").Should().BeFalse("a copy's limit does not end at an instant: only a fresh copy helps");
            refused.Headers.Contains("Retry-After").Should().BeFalse();

            (await WritesOfAsync(_demo, copy)).Should().Be(Limit, "a refused change is not counted: the count stops at the limit");
        }

        // A copy at its limit can still be looked at, and its owner can still sign out of every
        // session: a demo's cap never refuses that.
        using var read = await owner.RequestAsync(HttpMethod.Get, "/api/auth/me");
        using var signedOut = await owner.SignOutEverywhereAsync();
        using (new AssertionScope())
        {
            read.StatusCode.Should().Be(HttpStatusCode.OK);
            signedOut.StatusCode.Should().Be(HttpStatusCode.OK);
            (await WritesOfAsync(_demo, copy)).Should().Be(Limit, "neither is a change of the copy's");
        }
    }

    // CONTROL: green before this change. Where the demo is off there is no budget: a copy's owner,
    // on a host that does not turn the demo on, makes more changes than the limit and none is
    // counted or refused. It is what fails if the middleware counts whatever the flag says.
    [Fact]
    public async Task WithTheDemoOff_ACopysOwner_IsNeitherCountedNorRefused()
    {
        const string password = "Given-Pass-2026!";
        var copy = await HandMadeDemoCopy.CreateAsync(_ordinary);
        using (var scope = _ordinary.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(copy.Owner.Id.ToString());
            (await users.AddPasswordAsync(user!, password)).Succeeded.Should().BeTrue("ARRANGE: a free copy's owner has no password");
        }

        using var client = _ordinary.CreateClient();
        var owner = await DemoVisitor.SignInAsync(client, copy.Owner.Email!, password);

        for (var change = 1; change <= Limit + 2; change++)
        {
            using var verified = await owner.VerifyPinAsync(DemoPin);
            verified.StatusCode.Should().Be(HttpStatusCode.OK, "change {0}: where the demo is off nothing is refused for a budget", change);
        }

        (await WritesOfAsync(_ordinary, copy)).Should().Be(0, "and nothing is counted");
    }

    // ── Before idempotency ───────────────────────────────────────────────────────────────────────

    // The budget's place before idempotency, held without a database. A deposit with no
    // idempotency key is refused by the idempotency middleware, which looks at nothing else of
    // the request, so which of the two answers it says which is asked first. Under the limit the
    // budget counts it and idempotency refuses it; at the limit the budget refuses it and the key
    // is never asked for. With the two the other way round it is refused for its key both times
    // and never counted.
    [Fact]
    public async Task WithTheDemoOn_ADepositWithNoKey_IsCountedBeforeItsKeyIsAskedFor()
    {
        using var client = _demo.CreateClient();
        var (copy, owner) = await ClaimACopyAsync(client);

        using var noKey = await owner.RequestAsync(HttpMethod.Post, "/api/transactions/deposit");
        var counted = await WritesOfAsync(_demo, copy);
        await SetWritesAsync(_demo, copy, Limit);
        using var atTheLimit = await owner.RequestAsync(HttpMethod.Post, "/api/transactions/deposit");

        using (new AssertionScope())
        {
            noKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await DemoVisitor.ErrorCodeOfAsync(noKey)).Should().Be(ErrorCodes.IdempotencyKeyMissing);
            counted.Should().Be(1, "the budget is asked before the key is");
            atTheLimit.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "at the limit the budget answers before the key is asked for");
            (await DemoVisitor.ErrorCodeOfAsync(atTheLimit)).Should().Be(ErrorCodes.DemoCopyLimit);
            (await WritesOfAsync(_demo, copy)).Should().Be(Limit, "and the refused one is not counted");
        }
    }

    // ── The reveal ───────────────────────────────────────────────────────────────────────────────

    private static string RevealOf(Guid accountId) => $"/api/accounts/{accountId}/full-number";

    [Fact]
    public async Task WithTheDemoOn_TheReveal_IsCountedThoughItIsAGet_AndRefusedAtTheLimit_AndAnotherReadOfTheAccountIsNeither()
    {
        using var client = _demo.CreateClient();
        var (copy, owner) = await ClaimACopyAsync(client);

        // An account that is not there: the answer is the action's 404 either way, and what is
        // shown is whether the request was counted before it got that far.
        var account = Guid.CreateVersion7();

        using var revealed = await owner.RevealAsync(account);
        var afterTheReveal = await WritesOfAsync(_demo, copy);
        using var looked = await owner.RequestAsync(HttpMethod.Get, $"/api/accounts/{account}");
        var afterTheRead = await WritesOfAsync(_demo, copy);

        using (new AssertionScope())
        {
            revealed.StatusCode.Should().Be(HttpStatusCode.NotFound, "ARRANGE: no such account");
            afterTheReveal.Should().Be(1, "the reveal is a GET that writes, and it is counted as a change");
            looked.StatusCode.Should().Be(HttpStatusCode.NotFound, "ARRANGE: no such account");
            afterTheRead.Should().Be(1, "CONTROL: a GET that writes nothing is not counted");
        }

        await SetWritesAsync(_demo, copy, Limit);
        using var refused = await owner.RevealAsync(account);
        using var stillLooked = await owner.RequestAsync(HttpMethod.Get, $"/api/accounts/{account}");

        using (new AssertionScope())
        {
            refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "at the limit the reveal is refused before its action runs");
            (await DemoVisitor.ErrorCodeOfAsync(refused)).Should().Be(ErrorCodes.DemoCopyLimit);
            stillLooked.StatusCode.Should().Be(HttpStatusCode.NotFound, "CONTROL: a read is answered as it was");
            (await WritesOfAsync(_demo, copy)).Should().Be(Limit);
        }
    }

    public static TheoryData<string, bool> OtherMethods() => new()
    {
        // the method, whether it is one that changes something
        { "POST", true },
        { "PUT", true },
        { "PATCH", true },
        { "DELETE", true },

        // CONTROL: green before this change, these two rows. A method that changes nothing is
        // answered as it was.
        { "HEAD", false },
        { "OPTIONS", false },
    };

    // What the host answers another method on the reveal's path, so that it is known. The marker
    // is on the endpoint, which is GET, and not on its path: another method never reaches that
    // endpoint, and is answered 405 and told which method the path takes. The request has an
    // endpoint all the same, the one that answers 405, so on the demo a signed-in user's POST,
    // PUT, PATCH or DELETE there is counted like any other request that could change something,
    // and refused at the limit before the 405; a HEAD or an OPTIONS is neither.
    [Theory]
    [MemberData(nameof(OtherMethods))]
    public async Task WithTheDemoOn_AnotherMethodOnTheRevealsPath_Is405_AndCountedOnlyIfItIsOneThatChanges(string method, bool changes)
    {
        using var client = _demo.CreateClient();
        var (copy, owner) = await ClaimACopyAsync(client);
        var path = RevealOf(Guid.CreateVersion7());

        using var response = await owner.RequestAsync(new HttpMethod(method), path);
        var counted = await WritesOfAsync(_demo, copy);
        await SetWritesAsync(_demo, copy, Limit);
        using var atTheLimit = await owner.RequestAsync(new HttpMethod(method), path);

        using (new AssertionScope())
        {
            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            response.Content.Headers.Allow.Should().Equal("GET");
            counted.Should().Be(changes ? 1 : 0);
            atTheLimit.StatusCode.Should().Be(changes ? HttpStatusCode.TooManyRequests : HttpStatusCode.MethodNotAllowed);
            (await WritesOfAsync(_demo, copy)).Should().Be(Limit);
        }
    }

    // CONTROL: green before this change. With the demo off the same six are 405 and nothing more.
    [Theory]
    [MemberData(nameof(OtherMethods))]
    public async Task WithTheDemoOff_AnotherMethodOnTheRevealsPath_Is405(string method, bool changes)
    {
        _ = changes;
        using var client = _ordinary.CreateClient();
        var (user, _, _) = await DemoVisitor.RegisterAsync(client, "outside");

        using var response = await user.RequestAsync(new HttpMethod(method), RevealOf(Guid.CreateVersion7()));

        using (new AssertionScope())
        {
            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            response.Content.Headers.Allow.Should().Equal("GET");
        }
    }
}
