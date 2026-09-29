using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Api.Observability;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The API's request deadline and the commit gate (ADR-0058): a request that is still waiting on
/// the database when its deadline fires is cancelled and answered 503, and once a commit has started
/// nothing cancels it, so what committed is answered, stored and replayed whole.
/// </summary>
/// <remarks>
/// <para>
/// The waits are made on the server (<see cref="WaitForInterceptor"/>), so the cancellation under
/// test is SqlClient's own: the attention, and what EF surfaces when a running command is
/// cancelled. A deadline of a few seconds is set on a second host over the same database, and every
/// step that is not under test (registration, funding, minting, retries) goes through a host with the
/// production deadline, so a slow first request cannot be mistaken for the deadline.
/// </para>
/// <para>
/// Measured before this change, with the same holds: the read answered 200 after 20 s and the held
/// transfer 201 after 20 s, because no token reached the database.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class RequestDeadlineSqlServerTests : IDisposable
{
    private const string Pin = "123456";
    private const string Password = "TestPass123!";

    /// <summary>How long a held command sits on the server: far past any deadline set here.</summary>
    private static readonly TimeSpan Held = TimeSpan.FromSeconds(20);

    /// <summary>How long SQL Server may take to acknowledge a cancelled command.</summary>
    private static readonly TimeSpan Attention = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ITestOutputHelper _output;
    private readonly List<CustomWebApplicationFactory> _factories = [];

    public RequestDeadlineSqlServerTests(ITestOutputHelper output) => _output = output;

    private CustomWebApplicationFactory Host(
        int? deadlineSeconds = null, LogEventLevel captureFrom = LogEventLevel.Warning, bool retrying = false)
    {
        var factory = new CustomWebApplicationFactory();
        factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        factory.CaptureLog(captureFrom);
        if (deadlineSeconds is { } seconds)
        {
            factory.SetRequestDeadlineSeconds(seconds);
        }

        if (retrying)
        {
            factory.EnableSqlRetryOnFailure();
        }

        _factories.Add(factory);
        _ = factory.CreateClient();
        return factory;
    }

    [SqlServerFact]
    public async Task AReadHeldPastTheDeadline_Answers503_WithinTheDeadlineAndTheAttention()
    {
        var setup = Host();
        var user = await RegisterAsync(setup.CreateClient(), "dlr", withPin: false);

        const int deadline = 2;
        var deadlined = Host(deadline);
        var client = deadlined.CreateClient();
        _ = await GetAsync(client, user, "/api/accounts"); // warms the host: its result is not the subject

        var wait = WaitForInterceptor.OnCommandContaining(Held, "FROM [Accounts]");
        deadlined.AddInterceptor(wait);

        var clock = Stopwatch.StartNew();
        var response = await GetAsync(client, user, "/api/accounts");
        clock.Stop();
        Print("held read", response, clock, deadlined);

        wait.Fired.Should().BeTrue("the proof is void unless the read was actually held on the server");
        await DatabaseUnavailableSqlServerTests.AssertServiceUnavailableAsync(response, applied: null);
        clock.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(deadline) + Attention + TimeSpan.FromSeconds(2),
            "the deadline cancels the command; only the attention may add to it");
    }

    [SqlServerFact]
    public async Task ATransferHeldBeforeItsCommit_Answers503AppliedFalse_AndTheSameKeyThenMovesTheMoneyOnce()
    {
        var setup = Host();
        var setupClient = setup.CreateClient();
        var sender = await RegisterAsync(setupClient, "dls", withPin: true);
        var recipient = await RegisterAsync(setupClient, "dlt", withPin: false);
        (await DepositAsync(setupClient, sender, 1000m, Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.Created);

        var body = new TransferRequest
        {
            FromAccountId = sender.AccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Deadline proof",
        };
        var authorization = await MintAsync(setupClient, sender, "/api/transfers/authorizations", new TransferAuthorizationRequest
        {
            FromAccountId = body.FromAccountId,
            RecipientAzureTag = body.RecipientAzureTag,
            Amount = body.Amount,
            Pin = Pin,
        });

        const int deadline = 3;
        var deadlined = Host(deadline);
        var client = deadlined.CreateClient();
        _ = await GetAsync(client, sender, "/api/accounts"); // warms the host

        // The transfer's ledger rows, written inside its transaction, before its commit.
        var wait = new WaitForInterceptor(
            text => text.Contains("[Transactions]", StringComparison.Ordinal)
                    && text.Contains("INSERT", StringComparison.Ordinal),
            Held);
        deadlined.AddInterceptor(wait);

        var key = Guid.NewGuid();
        var clock = Stopwatch.StartNew();
        var response = await PostMonetaryAsync(client, sender, "/api/transfers", body, key, authorization);
        clock.Stop();
        Print("held transfer", response, clock, deadlined);

        wait.Fired.Should().BeTrue("the proof is void unless the transfer was held inside its transaction");
        await DatabaseUnavailableSqlServerTests.AssertServiceUnavailableAsync(response, applied: false);
        clock.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(deadline) + Attention + TimeSpan.FromSeconds(3) + TimeSpan.FromSeconds(2),
            "the deadline, the attention, and the 3-second release of the claim");

        (await BalanceAsync(setup, sender.AccountId)).Should().Be(1000m, "applied: false means nothing moved");
        (await BalanceAsync(setup, recipient.AccountId)).Should().Be(0m);
        (await LedgerRowsAsync(setup, sender.AccountId)).Should().Be(1, "the funding deposit only");
        (await LedgerRowsAsync(setup, recipient.AccountId)).Should().Be(0);
        (await RecordAsync(setup, sender.UserId, "POST api/transfers", key)).Should().BeNull(
            "the claim is released, so the same key can be sent again at once");

        var retry = await PostMonetaryAsync(setupClient, sender, "/api/transfers", body, key, authorization);
        _output.WriteLine($"same key, same authorisation: {(int)retry.StatusCode} {await retry.Content.ReadAsStringAsync()}");

        retry.StatusCode.Should().Be(HttpStatusCode.Created, "the same key and the same authorisation now execute");
        (await BalanceAsync(setup, sender.AccountId)).Should().Be(900m, "debited exactly once");
        (await BalanceAsync(setup, recipient.AccountId)).Should().Be(100m, "credited exactly once");
        (await LedgerRowsAsync(setup, sender.AccountId)).Should().Be(2, "funding and ONE transfer out");
        (await LedgerRowsAsync(setup, recipient.AccountId)).Should().Be(1, "ONE transfer in");
    }

    [SqlServerFact]
    public async Task ACommitThatFails_TurnsTheDeadlineBackOn_SoTheRetryIsCancelledInTime()
    {
        // The gate turns the deadline off while a commit runs; a commit that fails must turn it back
        // on, at its original instant, or the execution strategy's retry of the whole transfer runs
        // with nothing able to cancel it. The commit fails as it starts, after the gate has let it
        // start (the gate is the host's own interceptor, so it runs before the test's), with a
        // transient fault the retrying strategy re-runs; the retry's first read of the claim is then
        // held on the server far past the deadline. Without the re-arm, that read finishes after
        // 20 s and the transfer answers 201.
        var setup = Host();
        var setupClient = setup.CreateClient();
        var sender = await RegisterAsync(setupClient, "dlc", withPin: true);
        var recipient = await RegisterAsync(setupClient, "dld", withPin: false);
        (await DepositAsync(setupClient, sender, 1000m, Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.Created);

        var body = new TransferRequest
        {
            FromAccountId = sender.AccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Failed commit proof",
        };
        var authorizationRequest = new TransferAuthorizationRequest
        {
            FromAccountId = body.FromAccountId,
            RecipientAzureTag = body.RecipientAzureTag,
            Amount = body.Amount,
            Pin = Pin,
        };
        var warmUpAuthorization = await MintAsync(setupClient, sender, "/api/transfers/authorizations", authorizationRequest);
        var authorization = await MintAsync(setupClient, sender, "/api/transfers/authorizations", authorizationRequest);

        const int deadline = 3;
        var deadlined = Host(deadline, retrying: true);
        var fault = new TransferTransientFault(TransferFaultMode.BeforeCommit);
        deadlined.AddInterceptor(new TransferCommandFaultInterceptor(fault));
        deadlined.AddInterceptor(new TransferCommitFaultInterceptor(fault));
        var client = deadlined.CreateClient();

        // One whole transfer first, fault not armed: the one under test then reaches its commit well
        // inside the deadline, so the deadline cannot fire before the gate is entered.
        (await PostMonetaryAsync(client, sender, "/api/transfers", body, Guid.NewGuid(), warmUpAuthorization))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var wait = new WaitForInterceptor(
            text => fault.Fired && text.Contains("FROM [IdempotencyRecords]", StringComparison.Ordinal),
            Held);
        deadlined.AddInterceptor(wait);
        fault.Arm();

        var key = Guid.NewGuid();
        var clock = Stopwatch.StartNew();
        var response = await PostMonetaryAsync(client, sender, "/api/transfers", body, key, authorization);
        clock.Stop();
        Print("failed commit, held retry", response, clock, deadlined);

        fault.Fired.Should().BeTrue("the proof is void unless a commit the gate let start then failed");
        wait.Fired.Should().BeTrue("and unless the strategy's retry was held on the server");

        // No applied: a commit was let start, and a commit that fails may still have landed.
        await DatabaseUnavailableSqlServerTests.AssertServiceUnavailableAsync(response, applied: null);
        clock.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(deadline) + Attention + TimeSpan.FromSeconds(3) + TimeSpan.FromSeconds(2),
            "the deadline came back on at its original instant: the deadline, the attention, and the "
            + "3-second release of the claim");

        (await BalanceAsync(setup, sender.AccountId)).Should().Be(900m, "the warm-up transfer only");
        (await BalanceAsync(setup, recipient.AccountId)).Should().Be(100m);
        (await LedgerRowsAsync(setup, sender.AccountId)).Should().Be(2, "the funding deposit and the warm-up");
        (await LedgerRowsAsync(setup, recipient.AccountId)).Should().Be(1);
    }

    [SqlServerFact]
    public async Task EachSuccessfulMoneyRequest_EntersTheCommitGateExactlyOnce()
    {
        // The gate decides at each commit of the request's own context whether it may start. The
        // money moves in exactly one of them; the claim, the stored answer and the counters are
        // single statements outside any transaction. More than one entry would mean a money request
        // commits in two steps; none, that its commit escaped the gate.
        var host = Host();
        var client = host.CreateClient();
        var user = await RegisterAsync(client, "gte", withPin: true);
        var recipient = await RegisterAsync(client, "gtr", withPin: false);
        var savings = await CreateSecondAccountAsync(client, user);

        using var gate = new GateEntries();
        var entries = new Dictionary<string, long>();

        entries["deposit"] = await gate.DuringAsync(async () =>
            (await DepositAsync(client, user, 500m, Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.Created));

        var withdrawal = await MintAsync(client, user, "/api/transactions/withdraw/authorizations",
            new WithdrawalAuthorizationRequest { AccountId = user.AccountId, Amount = 10m, Pin = Pin });
        entries["withdraw"] = await gate.DuringAsync(async () =>
            (await PostMonetaryAsync(client, user, "/api/transactions/withdraw",
                new WithdrawRequest { AccountId = user.AccountId, Amount = 10m }, Guid.NewGuid(), withdrawal))
            .StatusCode.Should().Be(HttpStatusCode.Created));

        var transfer = new TransferRequest
        {
            FromAccountId = user.AccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 20m,
            Description = "Gate proof",
        };
        var external = await MintAsync(client, user, "/api/transfers/authorizations", new TransferAuthorizationRequest
        {
            FromAccountId = transfer.FromAccountId,
            RecipientAzureTag = transfer.RecipientAzureTag,
            Amount = 20m,
            Pin = Pin,
        });
        entries["transfer"] = await gate.DuringAsync(async () =>
            (await PostMonetaryAsync(client, user, "/api/transfers", transfer, Guid.NewGuid(), external))
            .StatusCode.Should().Be(HttpStatusCode.Created));

        var move = new InternalTransferRequest
        {
            FromAccountId = user.AccountId,
            ToAccountId = savings,
            Amount = 30m,
            Description = "Gate proof",
        };
        var internalAuthorization = await MintAsync(client, user, "/api/transfers/internal/authorizations",
            new InternalTransferAuthorizationRequest { FromAccountId = user.AccountId, ToAccountId = savings, Amount = 30m, Pin = Pin });
        entries["internal transfer"] = await gate.DuringAsync(async () =>
            (await PostMonetaryAsync(client, user, "/api/transfers/internal", move, Guid.NewGuid(), internalAuthorization))
            .StatusCode.Should().Be(HttpStatusCode.Created));

        _output.WriteLine("gate entries: " + string.Join(", ", entries.Select(e => $"{e.Key} {e.Value}")));

        entries.Should().BeEquivalentTo(
            new Dictionary<string, long> { ["deposit"] = 1, ["withdraw"] = 1, ["transfer"] = 1, ["internal transfer"] = 1 },
            "each successful money request commits through the gate exactly once "
            + "(the counter azurebank.commit_gate.entered)");
    }

    [SqlServerFact]
    public async Task AStuckResponseStore_DoesNotHoldTheAnswer_AndTheKeyStaysInFlight()
    {
        // After the commit the answer is true and is sent: storing it for replay gets 3 s and no
        // more. The record stays Executed, and the same key is IN_FLIGHT (then RESULT_UNKNOWN once
        // stale), never an empty replay and never a second execution.
        var host = Host();
        var client = host.CreateClient();
        var user = await RegisterAsync(client, "rsp", withPin: false);
        (await DepositAsync(client, user, 100m, Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.Created); // warm

        var wait = new WaitForInterceptor(
            text => text.Contains("UPDATE [IdempotencyRecords]", StringComparison.Ordinal)
                    && text.Contains("[ResponseBody]", StringComparison.Ordinal),
            Held);
        host.AddInterceptor(wait);

        var key = Guid.NewGuid();
        var clock = Stopwatch.StartNew();
        var response = await DepositAsync(client, user, 50m, key);
        clock.Stop();
        var text = await response.Content.ReadAsStringAsync();
        Print("stuck response store", response, clock, host);

        wait.Fired.Should().BeTrue("the proof is void unless storing the answer was actually held");
        response.StatusCode.Should().Be(HttpStatusCode.Created, "the deposit committed");
        JsonSerializer.Deserialize<JsonElement>(text).GetProperty("data").GetProperty("newBalance").GetDecimal()
            .Should().Be(150m, "the answer is sent whole");
        clock.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(2 + 3 + 2),
            "the request (2 s, warm), the 3-second budget for storing its answer, and 2 s of slack");

        var record = await RecordAsync(host, user.UserId, "POST api/transactions/deposit", key);
        record.Should().NotBeNull("the deposit committed with its key");
        record!.Status.Should().Be(IdempotencyStatus.Executed, "the answer was not stored, and the commit happened");

        var again = await DepositAsync(client, user, 50m, key);
        var againText = await again.Content.ReadAsStringAsync();
        _output.WriteLine($"same key: {(int)again.StatusCode} {againText}");
        again.StatusCode.Should().Be(HttpStatusCode.Conflict, "never a second execution, never an empty replay");
        JsonSerializer.Deserialize<JsonElement>(againText).GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.IdempotencyInFlight);
    }

    [SqlServerFact]
    public async Task AClientThatHangsUpAfterTheCommit_GetsTheWholeAnswerOnItsRetry()
    {
        // Once a commit has started nothing cancels the request: the client hanging up after it
        // must not cut the answer that is stored for replay. Before, the formatter swallowed the
        // cancellation and stored whatever it had written, which could be nothing.
        var host = Host();
        var client = host.CreateClient();
        var user = await RegisterAsync(client, "hup", withPin: false);

        var hold = new HoldAfterCommit();
        host.AddInterceptor(hold);

        var key = Guid.NewGuid();
        using var hangUp = new CancellationTokenSource();
        var sent = DepositAsync(client, user, 75m, key, hangUp.Token);
        await hold.Committed.WaitAsync(TimeSpan.FromSeconds(30));

        hangUp.Cancel();
        var abandoned = await Record.ExceptionAsync(() => sent);
        abandoned.Should().BeAssignableTo<OperationCanceledException>("the client hung up");
        hold.Release();

        var stored = await WaitForStoredAsync(host, user.UserId, key);
        _output.WriteLine($"record after the hang-up: {stored?.Status.ToString() ?? "none"}, body '{stored?.ResponseBody}'");

        var replay = await DepositAsync(client, user, 75m, key);
        var text = await replay.Content.ReadAsStringAsync();
        _output.WriteLine($"replay: {(int)replay.StatusCode} '{text}'");

        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.Headers.TryGetValues(IdempotencyConstants.ReplayedHeaderName, out var replayed).Should().BeTrue();
        replayed!.Single().Should().Be("true");
        text.Should().NotBeNullOrWhiteSpace("the replay is the whole answer, not what was written before the hang-up");
        var data = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("data");
        data.GetProperty("newBalance").GetDecimal().Should().Be(75m);
        data.GetProperty("transaction").GetProperty("amount").GetDecimal().Should().Be(75m);
        (await LedgerRowsAsync(host, user.AccountId)).Should().Be(1, "the deposit ran once");
    }

    [SqlServerFact]
    public async Task AClientThatHangsUpBeforeTheCommit_GetsNothing_AndTheTransferRollsBack()
    {
        // The hang-up cancels the request's token, SqlClient cancels the held command, and what
        // reaches the handlers is whatever it makes of that: here a DbUpdateException, which the
        // exception handler's own short-circuit for a cancelled request does not recognise. It is
        // answered with nothing: 499, no 503 and no Error line for a client that is not there.
        var setup = Host();
        var setupClient = setup.CreateClient();
        var sender = await RegisterAsync(setupClient, "hus", withPin: true);
        var recipient = await RegisterAsync(setupClient, "hur", withPin: false);
        (await DepositAsync(setupClient, sender, 1000m, Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.Created);

        var body = new TransferRequest
        {
            FromAccountId = sender.AccountId,
            RecipientAzureTag = recipient.AzureTag,
            Amount = 100m,
            Description = "Hang-up proof",
        };
        var authorization = await MintAsync(setupClient, sender, "/api/transfers/authorizations", new TransferAuthorizationRequest
        {
            FromAccountId = body.FromAccountId,
            RecipientAzureTag = body.RecipientAzureTag,
            Amount = body.Amount,
            Pin = Pin,
        });

        var host = Host(captureFrom: LogEventLevel.Information);
        var client = host.CreateClient();
        _ = await GetAsync(client, sender, "/api/accounts"); // warms the host

        var wait = new WaitForInterceptor(
            text => text.Contains("[Transactions]", StringComparison.Ordinal)
                    && text.Contains("INSERT", StringComparison.Ordinal),
            Held);
        host.AddInterceptor(wait);

        var key = Guid.NewGuid();
        using var hangUp = new CancellationTokenSource();
        var sent = PostMonetaryAsync(client, sender, "/api/transfers", body, key, authorization, hangUp.Token);
        await WaitUntilAsync(() => wait.Fired, TimeSpan.FromSeconds(15));
        await Task.Delay(500); // the held statement is on the server
        hangUp.Cancel();
        (await Record.ExceptionAsync(() => sent)).Should().BeAssignableTo<OperationCanceledException>("the client hung up");

        var line = await RequestLineAsync(host, "POST", "/api/transfers");
        foreach (var captured in host.CapturedLog)
        {
            _output.WriteLine("  log " + captured);
        }

        wait.Fired.Should().BeTrue("the proof is void unless the transfer was held inside its transaction");
        line.Should().NotBeNull("the server finishes the abandoned request on its own and logs it");
        ((ScalarValue)line!.Properties["StatusCode"]).Value.Should().Be(499, "the client closed the request");
        host.CapturedEvents.Where(e => e.Level >= LogEventLevel.Error).Select(e => e.RenderMessage())
            .Should().BeEmpty("a client that hung up is not an error of the service");
        host.CapturedEvents.Where(e => e.MessageTemplate.Text.StartsWith("Service unavailable", StringComparison.Ordinal))
            .Should().BeEmpty("nothing was answered, so no outage was answered either");

        (await BalanceAsync(setup, sender.AccountId)).Should().Be(1000m, "the transfer rolled back");
        (await BalanceAsync(setup, recipient.AccountId)).Should().Be(0m);
        (await LedgerRowsAsync(setup, sender.AccountId)).Should().Be(1, "the funding deposit only");
        (await LedgerRowsAsync(setup, recipient.AccountId)).Should().Be(0);
        (await RecordAsync(setup, sender.UserId, "POST api/transfers", key)).Should().BeNull(
            "nothing committed, so the claim is released and the key can be sent again");
    }

    [SqlServerFact]
    public async Task ASignOutEverywhere_HeldPastTheDeadline_StillCommits()
    {
        // Logout is exempt ([NoRequestDeadline]): its revoke and its stamp commit together, and a
        // deadline could otherwise refuse that commit after both statements had run, leaving the
        // lever unpulled. Held on the server for three seconds under a one-second deadline.
        var setup = Host();
        var user = await RegisterAsync(setup.CreateClient(), "slo", withPin: false);

        var host = Host(deadlineSeconds: 1);
        var client = host.CreateClient();
        _ = await GetAsync(client, user, "/api/accounts"); // warms the host
        var wait = WaitForInterceptor.OnCommandContaining(TimeSpan.FromSeconds(3), "UPDATE", "[SessionStamp]");
        host.AddInterceptor(wait);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var clock = Stopwatch.StartNew();
        var response = await client.SendAsync(request);
        clock.Stop();
        Print("held logout", response, clock, host);

        wait.Fired.Should().BeTrue("the proof is void unless the stamp was held past the deadline");
        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(3), "the hold outlasted the one-second deadline");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = setup.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        (await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.UserId)).SessionStamp.Should().Be(1, "the stamp was raised");
        (await db.RefreshTokens.AsNoTracking().Where(t => t.UserId == user.UserId).ToListAsync())
            .Should().NotBeEmpty().And.OnlyContain(t => t.RevokedAt != null, "and every grant was revoked with it");
    }

    [SqlServerFact]
    public async Task ARevokeAndTheTripwire_HeldPastTheDeadline_StillLand()
    {
        // Revoke and refresh are exempt too. Neither passes a token to the database or commits a
        // transaction of the request's own, so this is a guard: it holds whether or not the
        // exemption is there, and fails if a later change hands them the request's token.
        var setup = Host();
        var user = await RegisterAsync(setup.CreateClient(), "srv", withPin: false);
        user.RefreshToken.Should().NotBeNull("registration issues the session's grant");

        var host = Host(deadlineSeconds: 1);
        var client = host.CreateClient();
        _ = await GetAsync(client, user, "/api/accounts"); // warms the host

        var revokeHold = WaitForInterceptor.OnCommandContaining(TimeSpan.FromSeconds(3), "UPDATE", "[RefreshTokens]");
        host.AddInterceptor(revokeHold);
        var clock = Stopwatch.StartNew();
        var revoked = await client.PostAsJsonAsync(
            "/api/auth/revoke", new RevokeRequest { RefreshTokens = [user.RefreshToken!] }, Json);
        clock.Stop();
        Print("held revoke", revoked, clock, host);

        revokeHold.Fired.Should().BeTrue();
        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(3));
        revoked.StatusCode.Should().Be(HttpStatusCode.OK);

        // The same grant again, after its session ended: the tripwire, whose audit row is held.
        var rowHold = WaitForInterceptor.OnCommandContaining(TimeSpan.FromSeconds(3), "INSERT INTO [AuditEvents]");
        host.AddInterceptor(rowHold);
        clock.Restart();
        var refused = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest { RefreshToken = user.RefreshToken! }, Json);
        clock.Stop();
        Print("held tripwire", refused, clock, host);

        rowHold.Fired.Should().BeTrue();
        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(3));
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var scope = setup.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var grant = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.UserId == user.UserId);
        grant.RevokedAt.Should().NotBeNull("the revoke landed");
        (await db.AuditEvents.AsNoTracking().CountAsync(
                e => e.Event == SecurityEvents.RefreshTokenReuse && e.SubjectId == grant.Id))
            .Should().Be(1, "the tripwire's row landed");
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Holds the first commit of a ledger row after it has landed, until released: the request is
    /// past the point of no return and has not answered yet. Matched on a tracked
    /// <c>Transaction</c> entity, so a background job's commit cannot take the one-shot hold.
    /// </summary>
    private sealed class HoldAfterCommit : DbTransactionInterceptor
    {
        private readonly TaskCompletionSource _committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed = 1;

        public Task Committed => _committed.Task;

        public void Release() => _released.TrySetResult();

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<AzureBank.Shared.Entities.Transaction>().Any() == true
                && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                _committed.TrySetResult();
                await _released.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None)
                    .ContinueWith(_ => { }, TaskScheduler.Default);
            }

            await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }

    /// <summary>Counts <c>azurebank.commit_gate.entered</c> on the API's meter.</summary>
    private sealed class GateEntries : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _count;

        public GateEntries()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ApiMetrics.MeterName && instrument.Name == "azurebank.commit_gate.entered")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _count, value));
            _listener.SetMeasurementEventCallback<int>((_, value, _, _) => Interlocked.Add(ref _count, value));
            _listener.Start();
        }

        public async Task<long> DuringAsync(Func<Task> request)
        {
            var before = Interlocked.Read(ref _count);
            await request();
            return Interlocked.Read(ref _count) - before;
        }

        public void Dispose() => _listener.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private sealed record TestUser(string Token, Guid UserId, Guid AccountId, string AzureTag, string? RefreshToken);

    private static async Task<TestUser> RegisterAsync(HttpClient client, string prefix, bool withPin)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var azureTag = $"{prefix}_{unique}";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = azureTag,
            Email = $"{prefix}{unique}@example.com",
            Password = Password,
            FirstName = "Dead",
            LastName = "Line",
        }, Json);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        var user = new TestUser(
            result!.Data!.Token.AccessToken, result.Data.User.Id, result.Data.Account.Id, azureTag,
            result.Data.Token.RefreshToken);

        if (withPin)
        {
            using var setPin = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin")
            {
                Content = JsonContent.Create(new SetPinRequest { Pin = Pin, Password = Password }, options: Json),
            };
            setPin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
            (await client.SendAsync(setPin)).EnsureSuccessStatusCode();
        }

        return user;
    }

    private static async Task<Guid> CreateSecondAccountAsync(HttpClient client, TestUser user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/accounts")
        {
            Content = JsonContent.Create(new CreateAccountRequest { Name = "Savings", Type = AccountType.Savings }, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, TestUser user, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DepositAsync(
        HttpClient client, TestUser user, decimal amount, Guid key, CancellationToken cancellationToken = default) =>
        PostMonetaryAsync(
            client, user, "/api/transactions/deposit",
            new DepositRequest { AccountId = user.AccountId, Amount = amount, Description = "Deadline proof" },
            key, stepUpAuthorizationId: null, cancellationToken);

    private static Task<HttpResponseMessage> PostMonetaryAsync<T>(
        HttpClient client, TestUser user, string url, T body, Guid key, Guid? stepUpAuthorizationId,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        request.Headers.Add(IdempotencyConstants.HeaderName, key.ToString());
        if (stepUpAuthorizationId is { } authorization)
        {
            request.Headers.Add(StepUpConstants.HeaderName, authorization.ToString());
        }

        return client.SendAsync(request, cancellationToken);
    }

    private static async Task<Guid> MintAsync<T>(HttpClient client, TestUser user, string url, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(Json))!.Data!.AuthorizationId;
    }

    private static async Task<decimal> BalanceAsync(CustomWebApplicationFactory host, Guid accountId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId)).Balance;
    }

    private static async Task<int> LedgerRowsAsync(CustomWebApplicationFactory host, Guid accountId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.Transactions.AsNoTracking().CountAsync(t => t.AccountId == accountId);
    }

    private static async Task<AzureBank.Shared.Entities.IdempotencyRecord?> RecordAsync(
        CustomWebApplicationFactory host, Guid userId, string endpoint, Guid key)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(r => r.UserId == userId && r.Endpoint == endpoint && r.Key == key);
    }

    /// <summary>
    /// The deposit's record once the abandoned request has finished on its own: Completed, or
    /// whatever it still is after 15 s.
    /// </summary>
    private static async Task<AzureBank.Shared.Entities.IdempotencyRecord?> WaitForStoredAsync(
        CustomWebApplicationFactory host, Guid userId, Guid key)
    {
        var until = Stopwatch.StartNew();
        AzureBank.Shared.Entities.IdempotencyRecord? record;
        do
        {
            record = await RecordAsync(host, userId, "POST api/transactions/deposit", key);
            if (record?.Status == IdempotencyStatus.Completed)
            {
                return record;
            }

            await Task.Delay(100);
        }
        while (until.Elapsed < TimeSpan.FromSeconds(15));

        return record;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan within)
    {
        var until = Stopwatch.StartNew();
        while (!condition() && until.Elapsed < within)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// The request line of the first <paramref name="method"/> to <paramref name="routePattern"/>,
    /// once the server has written it (it finishes an abandoned request on its own), or null.
    /// </summary>
    private static async Task<LogEvent?> RequestLineAsync(CustomWebApplicationFactory host, string method, string routePattern)
    {
        var until = Stopwatch.StartNew();
        do
        {
            var line = host.CapturedEvents.FirstOrDefault(e =>
                e.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal)
                && e.Properties.TryGetValue("RequestMethod", out var m) && m is ScalarValue { Value: string verb } && verb == method
                && e.Properties.TryGetValue("RoutePattern", out var p) && p is ScalarValue { Value: string route } && route == routePattern);
            if (line is not null)
            {
                return line;
            }

            await Task.Delay(100);
        }
        while (until.Elapsed < TimeSpan.FromSeconds(20));

        return null;
    }

    private void Print(string what, HttpResponseMessage response, Stopwatch clock, CustomWebApplicationFactory host)
    {
        _output.WriteLine($"{what}: {(int)response.StatusCode} after {clock.ElapsedMilliseconds} ms: "
                          + response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        foreach (var line in host.CapturedLog)
        {
            _output.WriteLine("  log " + line);
        }
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }
}
