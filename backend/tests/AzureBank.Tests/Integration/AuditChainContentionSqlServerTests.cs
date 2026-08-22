using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What a SLOW audit store costs the money path — measured, because ADR-0044 D1 made every deposit,
/// withdrawal and transfer wait on the audit chain's tail lock and nothing had ever asked what that
/// costs when the audit store misbehaves rather than fails.
/// </summary>
/// <remarks>
/// <para>
/// The interesting question is not whether ONE slow audit write is slow. It is whether one slow
/// audit write blocks OTHER money movements that have nothing to do with it. The chain's tail is a
/// single row read under <c>UPDLOCK, HOLDLOCK</c>, so the lock is global to the table: every audited
/// save in the system queues on it. If a movement on a completely unrelated account has to wait, then
/// a merely SLOW audit store — not a broken one — degrades the whole bank, and the fail-closed trade
/// D1 accepts is larger than it was described as.
/// </para>
/// <para>
/// SQL-gated, because the property is entirely about real locks. The InMemory provider has none, so
/// this would report a comfortable answer there and mean nothing.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class AuditChainContentionSqlServerTests : IDisposable
{
    /// <summary>
    /// Long enough to dominate the measurement and be unmistakable in the numbers, short enough that
    /// the suite stays quick. Well under the 30-second CommandTimeout, so nothing here is testing the
    /// timeout — only the queueing.
    /// </summary>
    private static readonly TimeSpan HeldFor = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public AuditChainContentionSqlServerTests(ITestOutputHelper output) => _output = output;

    [SqlServerFact]
    public async Task OneSlowAuditWrite_DelaysAnUnrelatedAccountsDeposit()
    {
        var (client, firstAccount) = await CreateFundedClientAsync("slowa");
        var (other, secondAccount) = await CreateFundedClientAsync("slowb");

        // Everything above is warm-up. From here the interceptor is live, so the FIRST tail read
        // taken after this point is the one that stalls with the lock in hand.
        var stall = new SlowAuditTailInterceptor(HeldFor);
        _factory!.AddInterceptor(stall);

        var blocked = Stopwatch.StartNew();
        var slow = DepositAsync(client, firstAccount, 10m);

        /*
          WAIT FOR THE LOCK, DO NOT GUESS AT IT. This was a fixed 500 ms head start, on the
          assumption that the first deposit would reach its tail read within it. The interceptor is
          one-shot, so when that assumption failed — a cold start, a connection the pool still has to
          open, a loaded CI box — the SECOND deposit became the one that stalled ITSELF, and the
          assertion below stayed green while measuring a request waiting on its own delay rather than
          on another request's lock. Green for the wrong reason, which is the failure mode this
          suite exists to avoid.

          LockHeld completes only once a tail read has been caught with the lock already taken. The
          second deposit has not been sent at that point, so it cannot be the request holding it.
        */
        await stall.LockHeld.WaitAsync(TimeSpan.FromSeconds(10));

        var innocent = Stopwatch.StartNew();
        var second = await DepositAsync(other, secondAccount, 10m);
        innocent.Stop();

        (await slow).EnsureSuccessStatusCode();
        blocked.Stop();
        second.EnsureSuccessStatusCode();

        stall.Fired.Should().BeTrue("the test measures nothing if the tail read was never stalled");

        _output.WriteLine(
            $"audit tail held ~{HeldFor.TotalSeconds:0}s | slow deposit {blocked.ElapsedMilliseconds}ms "
            + $"| UNRELATED account's deposit {innocent.ElapsedMilliseconds}ms");

        /*
          THE ASSERTION IS DELIBERATELY THE UNCOMFORTABLE DIRECTION. It asserts that the unrelated
          deposit DOES wait, because that is what the design implies and what needed confirming. If
          this ever goes red because the second deposit came back fast, that is not a broken test —
          it means the contention model in ADR-0044 is wrong and the ADR needs rewriting, which is
          worth knowing either way.
        */
        innocent.ElapsedMilliseconds.Should().BeGreaterThan(
            1_000,
            "a deposit on a DIFFERENT account, by a DIFFERENT user, queues behind the chain tail "
            + "lock — the audit table is a single global choke point for every money movement");
    }

    [SqlServerFact]
    public async Task WhenTheTailCannotBeTaken_TheMovementIsRefusedFastRatherThanQueued()
    {
        /*
          THE OTHER HALF OF THE MEASUREMENT ABOVE, and the reason the bound exists.

          The first test proves the queue is real: a stalled tail read delays an unrelated account's
          deposit by nearly the whole stall. Left alone, the only thing bounding that queue was the
          global 30-second CommandTimeout — so a stuck audit store would have every money movement in
          the bank wait half a minute and then fail anyway.

          Audit:TailTimeoutSeconds bounds the wait on this one statement. The movement is still
          REFUSED — D1 is untouched, and no money moves without evidence — but it is refused in
          about a second instead of holding a connection and every other movement behind it.

          The bound is set to one second here purely so the proof is quick; production is five.
        */
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _factory.SetAuditTailTimeoutSeconds(1);
        _ = _factory.CreateClient();

        var (client, firstAccount) = await CreateFundedClientAsync("bounda");
        var (other, secondAccount) = await CreateFundedClientAsync("boundb");

        // Stall far longer than the bound, so the second movement provably cannot get the lock.
        var stall = new SlowAuditTailInterceptor(TimeSpan.FromSeconds(8));
        _factory.AddInterceptor(stall);

        var held = DepositAsync(client, firstAccount, 10m);

        // Same reason as the test above: wait until the lock is provably held by the only request in
        // flight, rather than assuming a fixed head start won the race.
        await stall.LockHeld.WaitAsync(TimeSpan.FromSeconds(10));

        var refused = Stopwatch.StartNew();
        var second = await DepositAsync(other, secondAccount, 10m);
        refused.Stop();

        stall.Fired.Should().BeTrue("the test measures nothing if the tail read was never stalled");
        _output.WriteLine(
            $"bound 1s, tail stalled 8s -> unrelated deposit answered {(int)second.StatusCode} "
            + $"in {refused.ElapsedMilliseconds}ms");

        second.StatusCode.Should().Be(
            HttpStatusCode.InternalServerError,
            "measured: the bounded tail read surfaces as a command timeout through "
            + "GlobalExceptionHandler. Pinned rather than left as 'not a success' so that a DIFFERENT "
            + "failure — a validation error, a rate limit — cannot quietly satisfy this test");

        refused.ElapsedMilliseconds.Should().BeLessThan(
            5_000,
            "the point of the bound is that it fails FAST — unbounded, this waited on the 30-second "
            + "command timeout while holding a connection and the rest of the money path behind it");

        /*
          AND THE INVARIANT THE COMMENT ABOVE CLAIMS, actually checked. "No money moves without
          evidence" was asserted only by the response not being a success — which a server fault
          unrelated to the audit chain would also satisfy. The balance is the claim; read it.
        */
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var balance = (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == secondAccount)).Balance;

            balance.Should().Be(
                0m,
                "the refused deposit must have moved nothing — a bounded audit failure that still "
                + "credited the account would be the exact state D1 exists to prevent");
        }

        await held; // let the stalled one finish so the fixture tears down cleanly
    }

    private async Task<(HttpClient Client, Guid AccountId)> CreateFundedClientAsync(string prefix)
    {
        _factory ??= CreateFactory();
        var client = _factory.CreateClient();

        var unique = prefix + Guid.NewGuid().ToString("N")[..6];
        var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            azureTag = unique,
            email = unique + "@example.com",
            password = "TestPass123!",
            firstName = "Contention",
            lastName = "Probe",
        });
        register.EnsureSuccessStatusCode();

        var data = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var token = data.GetProperty("token").GetProperty("accessToken").GetString();
        var accountId = data.GetProperty("account").GetProperty("id").GetGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, accountId);
    }

    private CustomWebApplicationFactory CreateFactory()
    {
        var factory = new CustomWebApplicationFactory();
        factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _ = factory.CreateClient();
        return factory;
    }

    private static Task<HttpResponseMessage> DepositAsync(HttpClient client, Guid accountId, decimal amount)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(new { accountId, amount }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    public void Dispose() => _factory?.Dispose();
}
