using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Entities;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The API's endpoints as the public demo changes them, through the host: what they answer with
/// <c>Demo:Enabled</c> false, as in every deployment that does not set it, and what they answer
/// with it true.
/// </summary>
/// <remarks>
/// <para>
/// Two hosts of its own, never a shared one: a default host, where the demo is off, and one with
/// <see cref="CustomWebApplicationFactory.EnableDemo"/>. Each test starts the one it asks.
/// </para>
/// <para>
/// On the InMemory provider, which has no transaction and no set-based statement: what is shown
/// here is the pipeline and the shape of the answers. That a copy is given once, that a failed
/// claim leaves nothing behind and what the caps count are shown on SQL Server
/// (<c>DemoClaimSqlServerTests</c>). A free copy here is made by hand
/// (<see cref="HandMadeDemoCopy"/>).
/// </para>
/// </remarks>
public sealed class DemoModeEndpointTests : IDisposable
{
    private const string ClaimPath = "/api/auth/demo/claim";

    /// <summary>A demo copy's password: four groups of four of the fifty-six characters.</summary>
    private const string CopyPassword = "^[A-HJ-NP-Za-kmnp-z2-9]{4}(-[A-HJ-NP-Za-kmnp-z2-9]{4}){3}$";

    private readonly CustomWebApplicationFactory _ordinary = new();
    private readonly CustomWebApplicationFactory _demo = new();

    public DemoModeEndpointTests() => _demo.EnableDemo();

    public void Dispose()
    {
        _ordinary.Dispose();
        _demo.Dispose();
    }

    /// <summary>A response as status, media type and body, without the one member that differs between two requests.</summary>
    /// <remarks>
    /// With parentheses where the body has braces. The assertion library builds a failure's message
    /// with <c>string.Format</c>, and a brace in either of two long texts that differ makes it throw
    /// <see cref="FormatException"/> in place of the message that shows where they differ; inside
    /// an assertion scope that exception is then lost behind the scope's own, with every assertion
    /// after it (FluentAssertions 8.8.0, measured on a sign-in's answer).
    /// </remarks>
    private static async Task<string> ShapeOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > 0 && JsonNode.Parse(body) is JsonObject json)
        {
            json.Remove("traceId");
            body = json.ToJsonString();
        }

        return $"{(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType} {body}"
            .Replace('{', '(').Replace('}', ')');
    }

    // ── With the demo off ────────────────────────────────────────────────────────────────────────

    // CONTROL: green before this change, when no route answered the path. With the action there,
    // [DemoOnly] is what keeps it green: without the marker the request reaches the action.
    [Fact]
    public async Task WithTheDemoOff_TheClaimLooksLikeAPathThatDoesNotExist()
    {
        using var client = _ordinary.CreateClient();
        var body = new DemoClaimRequest { ClientAddress = "203.0.113.7" };

        using var claim = await client.PostAsJsonAsync(ClaimPath, body);
        using var unknown = await client.PostAsJsonAsync("/api/auth/no-such-endpoint", body);

        using (new AssertionScope())
        {
            claim.StatusCode.Should().Be(HttpStatusCode.NotFound);
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound, "CONTROL: no route answers that path");
            // In parentheses: without them a response with no content type would skip the assertion.
            (claim.Content.Headers.ContentType?.MediaType).Should().Be("application/problem+json");

            using var claimBody = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
            using var unknownBody = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
            claimBody.RootElement.EnumerateObject().Select(p => p.Name)
                .Should().BeEquivalentTo(unknownBody.RootElement.EnumerateObject().Select(p => p.Name));
            claimBody.RootElement.GetProperty("title").GetString()
                .Should().Be(unknownBody.RootElement.GetProperty("title").GetString());

            // And everything else: status, media type, and each member but the trace id.
            (await ShapeOfAsync(claim)).Should().Be(
                await ShapeOfAsync(unknown),
                "with the demo off, a claim is answered as a path with no route is");
        }
    }

    public static TheoryData<string, string?, string?> MalformedClaims() => new()
    {
        { "an empty object", "{}", "application/json" },
        { "a client address of 65 characters", $$"""{"clientAddress":"{{new string('a', 65)}}"}""", "application/json" },
        { "a form post", "clientAddress=203.0.113.7", "application/x-www-form-urlencoded" },
        { "plain text", "203.0.113.7", "text/plain" },
        { "no body at all", null, null },
    };

    // CONTROL: green before this change, for the same reason. With the action there and no marker,
    // the binder answers these: 400 to a body it cannot bind, 415 to one that is not JSON.
    [Theory]
    [MemberData(nameof(MalformedClaims))]
    public async Task WithTheDemoOff_AMalformedClaim_IsStill404(string what, string? body, string? mediaType)
    {
        using var client = _ordinary.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, ClaimPath);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, mediaType!);
        }

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "{0}: with the demo off the endpoint is not there, so nothing reads the body ({1})",
            what, await response.Content.ReadAsStringAsync());
    }

    public static TheoryData<bool, string> OtherMethods()
    {
        var rows = new TheoryData<bool, string>();
        foreach (var demoOn in new[] { false, true })
        {
            foreach (var method in new[] { "GET", "HEAD", "PUT", "PATCH", "DELETE", "OPTIONS" })
            {
                rows.Add(demoOn, method);
            }
        }

        return rows;
    }

    // CONTROL: green before this change. It pins what the host answers today, so that it is known:
    // the 404 above hides the endpoint, which is POST, and not its path. Another method on the path
    // never reaches the action, so no marker of the action's is read; it is answered 405 and told
    // which method the path takes, with the demo off or on, as on the path of sign-in. What can
    // turn it red in this code: a middleware that refuses, with the demo off, an endpoint that
    // carries no marker (shown once: the six rows with the demo off then answer 404).
    [Theory]
    [MemberData(nameof(OtherMethods))]
    public async Task AnotherMethodOnTheClaimsPath_Is405WhateverTheFlag_AsOnSignInsPath(bool demoOn, string method)
    {
        using var client = (demoOn ? _demo : _ordinary).CreateClient();

        using var claim = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), ClaimPath));
        using var login = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/auth/login"));
        using var unknown = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/auth/no-such-endpoint"));

        using (new AssertionScope())
        {
            claim.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            claim.Content.Headers.Allow.Should().Equal("POST");
            login.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, "CONTROL: sign-in's path answers another method the same way");
            login.Content.Headers.Allow.Should().Equal("POST");
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound, "CONTROL: a path with no route is 404 under any method");
        }
    }

    [Fact]
    public async Task WithTheDemoOff_TheClaimsOwnCodeRefusesToo_AndTakesNoCopy()
    {
        // The pipeline never lets a request reach the claim with the demo off. Its service refuses
        // by itself all the same, as the pool's commands do: a copy that is somehow there is not
        // handed out by a deployment that is not the demo, whoever calls.
        var copy = await HandMadeDemoCopy.CreateAsync(_ordinary);
        using var scope = _ordinary.Services.CreateScope();
        var claims = scope.ServiceProvider.GetRequiredService<IDemoClaimService>();

        var act = () => claims.ClaimAsync(new DemoClaimRequest { ClientAddress = "203.0.113.7" });

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Demo:Enabled*");
        using (new AssertionScope())
        {
            (await copy.RowAsync(_ordinary)).ClaimedAt.Should().BeNull();
            (await copy.OwnerPasswordHashAsync(_ordinary)).Should().BeNull();
        }
    }

    // ── With the demo on ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithTheDemoOn_AndAnEmptyPool_TheClaimIs429PoolEmpty_WithNoRetryAfter()
    {
        using var client = _demo.CreateClient();

        using var response = await DemoVisitor.ClaimAsync(client);

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, text);
        using var body = JsonDocument.Parse(text);
        using (new AssertionScope())
        {
            body.RootElement.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.DemoPoolEmpty);
            body.RootElement.GetProperty("status").GetInt32().Should().Be(429);
            body.RootElement.TryGetProperty("retryAfterSeconds", out _).Should().BeFalse(
                "nobody can say when a copy will be free again");
            response.Headers.RetryAfter.Should().BeNull();
            response.Headers.Contains("Retry-After").Should().BeFalse();
        }
    }

    public static TheoryData<string, string> ClaimsWithoutAnAddress() => new()
    {
        { "no client address", "{}" },
        { "a null client address", """{"clientAddress":null}""" },
        { "an empty client address", """{"clientAddress":""}""" },
        { "a client address of white space", """{"clientAddress":"   "}""" },
        { "a client address of 65 characters", $$"""{"clientAddress":"{{new string('a', 65)}}"}""" },
    };

    [Theory]
    [MemberData(nameof(ClaimsWithoutAnAddress))]
    public async Task WithTheDemoOn_AClaimWithoutAClientAddress_Is400(string what, string json)
    {
        // A free copy is there, so a 400 is the body's and not an empty pool's.
        var copy = await HandMadeDemoCopy.CreateAsync(_demo);
        using var client = _demo.CreateClient();

        using var response = await client.PostAsync(ClaimPath, new StringContent(json, Encoding.UTF8, "application/json"));

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} ({1})", what, text);
        using var body = JsonDocument.Parse(text);
        using (new AssertionScope())
        {
            body.RootElement.TryGetProperty("errors", out var errors).Should().BeTrue("{0}: the 400 names what was wrong", what);
            errors.ValueKind.Should().Be(JsonValueKind.Object);
            (await copy.RowAsync(_demo)).ClaimedAt.Should().BeNull("a claim that is refused takes no copy");
        }
    }

    [Fact]
    public async Task WithTheDemoOn_AClaimFromOffTheTokenRoad_Is404_AndTakesNothing()
    {
        var copy = await HandMadeDemoCopy.CreateAsync(_demo);
        using var client = _demo.CreateClient();
        using var offTheRoad = new HttpRequestMessage(HttpMethod.Post, ClaimPath)
        {
            Content = JsonContent.Create(new DemoClaimRequest { ClientAddress = "203.0.113.7" }),
        };
        offTheRoad.Headers.Add(FakeRemoteAddressStartupFilter.HeaderName, "10.0.0.7");

        using var refused = await client.SendAsync(offTheRoad);

        using (new AssertionScope())
        {
            refused.StatusCode.Should().Be(HttpStatusCode.NotFound, "only the BFF's own client, over loopback, reaches a token endpoint");
            var row = await copy.RowAsync(_demo);
            row.ClaimedAt.Should().BeNull("nothing happened behind the 404");
            row.ClaimId.Should().BeNull();
            (await copy.OwnerPasswordHashAsync(_demo)).Should().BeNull();
        }

        // Its control: the same claim from the road takes that copy, so the 404 was the road's and
        // not the flag's, the body's or an empty pool's.
        using var fromTheRoad = await DemoVisitor.ClaimAsync(client);
        var claimed = await DemoVisitor.ClaimedAsync(fromTheRoad);
        claimed.User.Id.Should().Be(copy.Owner.Id);
    }

    [Fact]
    public async Task OnAFreeCopy_TheClaimAnswersTheTokensTheUserAndTheCopy_AndThePasswordSignsIn()
    {
        var copy = await HandMadeDemoCopy.CreateAsync(_demo);
        using var client = _demo.CreateClient();

        var before = DateTime.UtcNow;
        using var response = await DemoVisitor.ClaimAsync(client);
        var after = DateTime.UtcNow;

        var claim = await DemoVisitor.ClaimedAsync(response);
        var row = await copy.RowAsync(_demo);
        using (new AssertionScope())
        {
            (response.Headers.CacheControl?.NoStore).Should().BeTrue("the answer carries a password, and nothing may keep it");
            response.Headers.Pragma.ToString().Should().Be("no-cache");

            // The tokens, as a login answers them.
            claim.Token.AccessToken.Should().NotBeNullOrWhiteSpace();
            claim.Token.TokenType.Should().Be("Bearer");
            claim.Token.RefreshToken.Should().NotBeNullOrWhiteSpace();
            claim.Token.RefreshTokenExpiresAt.Should().NotBeNull().And.BeAfter(claim.Token.ExpiresAt.AddSeconds(-1));
            claim.Token.ExpiresAt.Should().BeAfter(after);
            claim.Token.SessionStamp.Should().Be(copy.Owner.SessionStamp);

            // The user: the copy's owner.
            claim.User.Id.Should().Be(copy.Owner.Id);
            claim.User.Email.Should().Be(copy.Owner.Email);
            claim.User.AzureTag.Should().Be(copy.Owner.AzureTag);
            claim.User.HasPin.Should().BeTrue("every user of a copy is seeded with the demo PIN");

            // The copy: what signs in to it again, whom it can pay, and when it ends.
            claim.Copy.Email.Should().Be(copy.Owner.Email);
            claim.Copy.Password.Should().MatchRegex(CopyPassword);
            claim.Copy.Pin.Should().Be("123456");
            claim.Copy.Contacts.Should().Equal(copy.ContactHandles);
            claim.Copy.Contacts.Should().BeInAscendingOrder(StringComparer.Ordinal).And.HaveCount(2);

            row.ClaimedAt.Should().NotBeNull();
            row.ClaimedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
            claim.Copy.ExpiresAt.Should().Be(row.ClaimedAt!.Value.AddHours(24), "a copy lives 24 hours from its claim by default");
            claim.Copy.ExpiresAt.Kind.Should().Be(DateTimeKind.Utc);
        }

        using var signIn = await DemoVisitor.TrySignInAsync(client, claim.Copy.Email, claim.Copy.Password);
        signIn.StatusCode.Should().Be(
            HttpStatusCode.OK, "the answered password signs in to the copy ({0})", await signIn.Content.ReadAsStringAsync());
        var signedIn = await signIn.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>(DemoVisitor.Json);
        signedIn!.Data!.User.Id.Should().Be(copy.Owner.Id);
    }

    // ── Who can sign in, with the demo on ────────────────────────────────────────────────────────

    /// <summary>A password that passes the sign-in request's own rule and Identity's.</summary>
    private const string GivenPassword = "Given-Pass-2026!";

    /// <summary>
    /// Gives a hand-made copy's owner a password through Identity, as something other than a claim
    /// would: a free copy has none.
    /// </summary>
    private static async Task GiveOwnerAPasswordAsync(CustomWebApplicationFactory api, HandMadeDemoCopy copy)
    {
        using var scope = api.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owner = await users.FindByIdAsync(copy.Owner.Id.ToString());
        owner.Should().NotBeNull("ARRANGE: the copy's owner exists");
        var added = await users.AddPasswordAsync(owner!, GivenPassword);
        added.Succeeded.Should().BeTrue(
            "ARRANGE: a free copy's owner has no password, so one can be added ({0})",
            string.Join("; ", added.Errors.Select(e => e.Description)));
    }

    /// <summary>The failed sign-ins counted for a user, as the database holds them now.</summary>
    private static async Task<int> FailedAttemptsOfAsync(CustomWebApplicationFactory api, Guid userId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.AccessFailedCount).SingleAsync();
    }

    // CONTROL: green before this change. It is the answer a deployment's smoke test reads after a
    // deploy (infra/deploy.py signs in with an address nobody can register and expects 401 with
    // this code): the demo's gate must leave it as it is.
    [Fact]
    public async Task WithTheDemoOn_AnUnknownEmail_IsStill401InvalidCredentials()
    {
        using var client = _demo.CreateClient();
        using var ordinaryClient = _ordinary.CreateClient();

        using var response = await DemoVisitor.TrySignInAsync(client, "deploy-smoke@azurebank.invalid", "Not-a-real-account-0");
        using var withTheDemoOff = await DemoVisitor.TrySignInAsync(ordinaryClient, "deploy-smoke@azurebank.invalid", "Not-a-real-account-0");

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, text);
        using var body = JsonDocument.Parse(text);
        using (new AssertionScope())
        {
            body.RootElement.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.InvalidCredentials);
            body.RootElement.GetProperty("detail").GetString().Should().Be("Invalid email or password.");
            (await ShapeOfAsync(response)).Should().Be(
                await ShapeOfAsync(withTheDemoOff), "the demo changes nothing in what an unknown email is answered");
        }
    }

    [Fact]
    public async Task WithTheDemoOn_OnlyTheOwnerOfAClaimedCopySignsIn()
    {
        // A claimed copy first, while it is the only one in the pool: the claim takes it.
        var claimed = await HandMadeDemoCopy.CreateAsync(_demo);
        using var client = _demo.CreateClient();
        using var claimResponse = await DemoVisitor.ClaimAsync(client);
        var claim = await DemoVisitor.ClaimedAsync(claimResponse);
        claim.User.Id.Should().Be(claimed.Owner.Id, "ARRANGE: the one free copy is the one claimed");

        // Then a free one, whose owner was given a password by something other than a claim.
        var free = await HandMadeDemoCopy.CreateAsync(_demo);
        await GiveOwnerAPasswordAsync(_demo, free);

        using var unknownEmail = await DemoVisitor.TrySignInAsync(client, "nobody@example.com", GivenPassword);
        using var asTheFreeOwner = await DemoVisitor.TrySignInAsync(client, free.Owner.Email!, GivenPassword);
        using var asAContact = await DemoVisitor.TrySignInAsync(client, claimed.Jane.Email!, GivenPassword);
        using var asTheOwner = await DemoVisitor.TrySignInAsync(client, claim.Copy.Email, claim.Copy.Password);

        using (new AssertionScope())
        {
            unknownEmail.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "CONTROL: an email nobody has is refused");
            var anUnknownEmails = await ShapeOfAsync(unknownEmail);

            asTheFreeOwner.StatusCode.Should().Be(
                HttpStatusCode.Unauthorized, "nobody claimed the copy, so its owner does not sign in, with the right password");
            (await ShapeOfAsync(asTheFreeOwner)).Should().Be(anUnknownEmails, "and is answered as an email nobody has");

            asAContact.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a copy's contact has no password");
            (await ShapeOfAsync(asAContact)).Should().Be(anUnknownEmails);
            (await FailedAttemptsOfAsync(_demo, claimed.Jane.Id)).Should().Be(
                0, "and its attempt is not counted toward a lock: it is refused before the password is looked at");

            asTheOwner.StatusCode.Should().Be(
                HttpStatusCode.OK, "CONTROL: the claimed copy's owner signs in ({0})", await asTheOwner.Content.ReadAsStringAsync());
        }

        // CONTROL: the same two users on a host with the demo off. The owner with a password signs
        // in, and the contact's attempt is counted, so what differs above is the demo's gate.
        var there = await HandMadeDemoCopy.CreateAsync(_ordinary);
        await GiveOwnerAPasswordAsync(_ordinary, there);
        using var ordinaryClient = _ordinary.CreateClient();
        using var ownerThere = await DemoVisitor.TrySignInAsync(ordinaryClient, there.Owner.Email!, GivenPassword);
        using var contactThere = await DemoVisitor.TrySignInAsync(ordinaryClient, there.Jane.Email!, GivenPassword);
        using (new AssertionScope())
        {
            ownerThere.StatusCode.Should().Be(HttpStatusCode.OK, await ownerThere.Content.ReadAsStringAsync());
            contactThere.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await FailedAttemptsOfAsync(_ordinary, there.Jane.Id)).Should().Be(1);
        }
    }
}
