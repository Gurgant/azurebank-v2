using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Api.Middleware;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Utilities;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A deposit is never answered 201 without its ledger row, even when its commit fails transiently
/// and EF's execution strategy runs the save again.
/// </summary>
/// <remarks>
/// <para>
/// The deposit's audited save (<c>AzureBankDbContext.SaveChangesAsync</c>) runs inside the execution
/// strategy: begin, chain the audit row, save, commit. The save accepted every change before the
/// commit. A transient fault at the commit rolled the transaction back, and the re-run found every
/// entry already <c>Unchanged</c>: it saved nothing, committed an empty transaction and answered 201,
/// while the server held neither the deposit nor its <c>Executed</c> flip.
/// </para>
/// <para>
/// The save now keeps its changes until the commit has landed, and asks the database whether the
/// owned audit row is there when a commit's outcome is unknown (ADR-0044). SQL-gated: the
/// fault is at a real transaction's commit, which the InMemory provider does not have.
/// </para>
/// <para>
/// Both faults are exercised: one as the commit starts, so nothing lands and the save must run
/// again; and one after the commit, so everything landed and the save must not run again. Each has
/// an asynchronous proof through the API and a synchronous one straight on the context, because the
/// two funnels are separate code. The lost acknowledgement is proved once more after the request
/// deadline has passed, on a PIN change, where the deadline cancels the strategy's question.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class DepositCommitFaultSqlServerTests : IDisposable
{
    private const string Password = "TestPass123!";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ITestOutputHelper _output;
    private readonly List<CustomWebApplicationFactory> _hosts = [];
    private CustomWebApplicationFactory? _factory;

    public DepositCommitFaultSqlServerTests(ITestOutputHelper output) => _output = output;

    [SqlServerFact]
    public async Task ADepositWhoseCommitFailsTransiently_IsAnswered201WithExactlyOneLedgerRow()
    {
        var (fault, response, rows, balance, record, auditRows) =
            await DepositUnderFaultAsync(TransferFaultMode.BeforeCommit);

        fault.Fired.Should().BeTrue("the proof is void unless the commit's start was actually faulted");
        response.StatusCode.Should().Be(HttpStatusCode.Created, "the strategy's re-run commits the deposit");
        rows.Should().Be(1, "a 201 for a deposit means its ledger row exists, exactly once");
        balance.Should().Be(250m, "the money a 201 announces is the money the account holds");
        record.Should().NotBeNull();
        record!.Status.Should().Be(IdempotencyStatus.Completed, "the deposit committed and its answer is stored for replay");
        auditRows.Should().Be(1, "the re-run writes the deposit's audit row, exactly once");
    }

    /// <summary>
    /// The other ambiguous commit: the deposit lands and the acknowledgement is lost. The save has to
    /// ask whether its audit row is there. Running it again would send rows the database already
    /// holds, and refuse a deposit that happened.
    /// </summary>
    [SqlServerFact]
    public async Task ADepositWhoseCommitAcknowledgementIsLost_IsAnswered201WithExactlyOneLedgerRow()
    {
        var (fault, response, rows, balance, record, auditRows) =
            await DepositUnderFaultAsync(TransferFaultMode.AfterCommit);

        fault.Fired.Should().BeTrue("the proof is void unless the commit's acknowledgement was actually lost");
        response.StatusCode.Should().Be(HttpStatusCode.Created, "the deposit committed, and the database says so when asked");
        rows.Should().Be(1, "a 201 for a deposit means its ledger row exists, exactly once");
        balance.Should().Be(250m, "the money a 201 announces is the money the account holds");
        record.Should().NotBeNull();
        record!.Status.Should().Be(IdempotencyStatus.Completed, "the deposit committed and its answer is stored for replay");
        auditRows.Should().Be(1, "the save that landed is not written a second time");
    }

    /// <summary>
    /// The synchronous funnel (<c>AzureBankDbContext.SaveChanges</c>) under the same two faults. No
    /// service saves through it today, and it is public, so it is held to the same answer.
    /// </summary>
    [SqlServerTheory]
    [InlineData(TransferFaultMode.BeforeCommit)]
    [InlineData(TransferFaultMode.AfterCommit)]
    public async Task AnAuditedSynchronousSave_WhoseCommitFails_LandsExactlyOnce(TransferFaultMode mode)
    {
        var fault = new TransferTransientFault(mode);
        var client = CreateFaultedClient(fault);
        var (_, userId, accountId) = await RegisterAsync(client);

        Guid ledgerId;
        bool pendingAfterSave;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();

            var account = db.Accounts.Single(a => a.Id == accountId);
            var ledger = new Transaction
            {
                Id = Guid.CreateVersion7(),
                TransactionNumber = IdGenerator.GenerateTransactionNumber(),
                AccountId = account.Id,
                Account = account,
                Type = TransactionType.Deposit,
                Amount = 250m,
                BalanceBefore = account.Balance,
                BalanceAfter = account.Balance + 250m,
                Description = "Synchronous commit fault proof",
                Status = TransactionStatus.Completed,
            };
            ledgerId = ledger.Id;
            account.Balance += 250m;
            db.Transactions.Add(ledger);
            audit.Record(
                SecurityEvents.MoneyDeposited, AuditOutcome.Succeeded,
                actorUserId: userId, subjectType: "Transaction", subjectId: ledger.Id);

            fault.Arm();
            db.SaveChanges();
            pendingAfterSave = db.ChangeTracker.HasChanges();
        }

        var (rows, balance, auditRows) = await ReadBackAsync(accountId, ledgerId);
        _output.WriteLine(
            $"{mode}: ledger rows {rows}; balance {balance}; audit rows {auditRows}; "
            + $"changes pending after the save {pendingAfterSave}");

        fault.Fired.Should().BeTrue("the proof is void unless the commit was actually faulted");
        rows.Should().Be(1, "a save that returned has written its ledger row, exactly once");
        balance.Should().Be(250m);
        auditRows.Should().Be(1, "and its audit row, exactly once");
        pendingAfterSave.Should().BeFalse("a save that returned has accepted its changes, as SaveChanges() promises");
    }

    /// <summary>
    /// A lost acknowledgement after the request deadline has passed, on a PIN change. The failed
    /// commit turns the deadline back on at an instant already gone, so it fires at once and the
    /// execution strategy cannot ask its question under the request's token: the save asks again
    /// under a budget of its own. The change landed either way; only the answer is under test.
    /// </summary>
    [SqlServerFact]
    public async Task APinChangeWhoseAcknowledgementIsLostPastTheDeadline_IsAnswered200_AndTheNewPinVerifies()
    {
        const int deadline = 3;
        var setup = Host();
        var setupClient = setup.CreateClient();
        var (token, userId, _) = await RegisterAsync(setupClient);
        (await SetPinAsync(setupClient, token, new SetPinRequest { Pin = "111111", Password = Password }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var fault = new LostAcknowledgementPastTheDeadline(TimeSpan.FromSeconds(deadline));
        var faulted = Host(deadline, fault);
        var client = faulted.CreateClient();

        // Warms the host with the same change, the fault not yet armed: its result is not the subject.
        (await SetPinAsync(client, token, new SetPinRequest { Pin = "222222", CurrentPin = "111111" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        fault.Arm();
        var clock = Stopwatch.StartNew();
        var response = await SetPinAsync(client, token, new SetPinRequest { Pin = "333333", CurrentPin = "222222" });
        clock.Stop();
        var body = await response.Content.ReadAsStringAsync();

        using var verify = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin/verify")
        {
            Content = JsonContent.Create(new VerifyPinRequest { Pin = "333333" }, options: Json),
        };
        verify.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var verified = JsonDocument.Parse(await (await setupClient.SendAsync(verify)).Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("verified").GetBoolean();

        using var scope = setup.Services.CreateScope();
        var changes = await scope.ServiceProvider.GetRequiredService<AzureBankDbContext>().AuditEvents.AsNoTracking()
            .CountAsync(e => e.Event == SecurityEvents.PinChanged && e.ActorUserId == userId);

        _output.WriteLine(
            $"answered {(int)response.StatusCode} after {clock.ElapsedMilliseconds} ms; deadline fired first "
            + $"{fault.DeadlineFiredFirst}; new PIN verifies {verified}; PinChanged rows {changes}; body {body}");

        fault.Fired.Should().BeTrue("the proof is void unless the change's acknowledgement was actually lost");
        fault.DeadlineFiredFirst.Should().BeTrue(
            "the proof is void unless the deadline had fired before the strategy came to ask");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the change landed, and the database says so when asked");
        verified.Should().BeTrue("the PIN a 200 announces is the PIN the account holds");
        changes.Should().Be(2, "the warm-up's change and this one, each written once");
    }

    private async Task<(TransferTransientFault Fault, HttpResponseMessage Response, int Rows, decimal Balance,
        IdempotencyRecord? Record, int AuditRows)> DepositUnderFaultAsync(TransferFaultMode mode)
    {
        var fault = new TransferTransientFault(mode);
        var client = CreateFaultedClient(fault);

        var (token, userId, accountId) = await RegisterAsync(client);

        fault.Arm();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(
                new DepositRequest { AccountId = accountId, Amount = 250m, Description = "Commit fault proof" },
                options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var rows = await db.Transactions.AsNoTracking().CountAsync(t => t.AccountId == accountId);
        var balance = (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId)).Balance;
        var record = await db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(r => r.UserId == userId && r.Endpoint == "POST api/transactions/deposit");
        var auditRows = await db.AuditEvents.AsNoTracking()
            .CountAsync(e => e.Event == SecurityEvents.MoneyDeposited && e.ActorUserId == userId);

        _output.WriteLine(
            $"{mode}: answered {(int)response.StatusCode}; ledger rows {rows}; balance {balance}; "
            + $"idempotency record {record?.Status.ToString() ?? "none"}; audit rows {auditRows}; body {body}");

        return (fault, response, rows, balance, record, auditRows);
    }

    private HttpClient CreateFaultedClient(TransferTransientFault fault)
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _factory.EnableSqlRetryOnFailure();
        _factory.AddInterceptor(new TransferCommandFaultInterceptor(fault));
        _factory.AddInterceptor(new TransferCommitFaultInterceptor(fault));
        return _factory.CreateClient();
    }

    private async Task<(int Rows, decimal Balance, int AuditRows)> ReadBackAsync(Guid accountId, Guid ledgerId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var rows = await db.Transactions.AsNoTracking().CountAsync(t => t.AccountId == accountId);
        var balance = (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId)).Balance;
        var auditRows = await db.AuditEvents.AsNoTracking().CountAsync(e => e.SubjectId == ledgerId);
        return (rows, balance, auditRows);
    }

    private static async Task<(string Token, Guid UserId, Guid AccountId)> RegisterAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"dcf_{unique}",
            Email = $"dcf{unique}@example.com",
            Password = Password,
            FirstName = "Commit",
            LastName = "Fault",
        }, Json);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        return (result!.Data!.Token.AccessToken, result.Data.User.Id, result.Data.Account.Id);
    }

    /// <summary>A host on the test database with retries on, as production has: a deadline and a fault when given.</summary>
    private CustomWebApplicationFactory Host(int? deadlineSeconds = null, IInterceptor? fault = null)
    {
        var host = new CustomWebApplicationFactory();
        host.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        host.EnableSqlRetryOnFailure();
        if (deadlineSeconds is { } seconds)
        {
            host.SetRequestDeadlineSeconds(seconds);
        }

        if (fault is not null)
        {
            host.AddInterceptor(fault);
        }

        _hosts.Add(host);
        return host;
    }

    private static async Task<HttpResponseMessage> SetPinAsync(HttpClient client, string token, SetPinRequest pin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin")
        {
            Content = JsonContent.Create(pin, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Loses the acknowledgement of the first commit after <see cref="Arm"/> of a request's own
    /// context that carries an audit row, after the request deadline has passed: it commits the
    /// transaction itself, waits out the deadline and throws a fault EF retries. It runs after the
    /// commit gate, so the gate has let the commit start and then hears it failed.
    /// </summary>
    private sealed class LostAcknowledgementPastTheDeadline(TimeSpan deadline) : DbTransactionInterceptor
    {
        private int _armed;
        private int _fired;
        private int _deadlineFiredFirst;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        /// <summary>The deadline had fired when EF went on to ask whether the commit landed.</summary>
        public bool DeadlineFiredFirst => Volatile.Read(ref _deadlineFiredFirst) == 1;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1
                && DeadlineOf(eventData.Context) is not null
                && eventData.Context!.ChangeTracker.Entries<AuditEvent>().Any(e => e.State == EntityState.Added)
                && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                await transaction.CommitAsync(CancellationToken.None);
                await Task.Delay(deadline + TimeSpan.FromMilliseconds(250), CancellationToken.None);
                throw new TimeoutException(
                    "Injected: the commit landed and its acknowledgement was lost after the request deadline (test).");
            }

            return result;
        }

        /// <summary>
        /// After the gate's own hook, which turned the deadline back on at an instant already past:
        /// waits for it to fire, so the strategy's question always meets a cancelled token.
        /// </summary>
        public override async Task TransactionFailedAsync(
            DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!Fired || DeadlineOf(eventData.Context) is not { } requestDeadline)
            {
                return;
            }

            var waited = Stopwatch.StartNew();
            while (!requestDeadline.Token.IsCancellationRequested && waited.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(10, CancellationToken.None);
            }

            if (requestDeadline.FiredByDeadline)
            {
                Interlocked.Exchange(ref _deadlineFiredFirst, 1);
            }
        }

        /// <summary>The deadline of the request whose scope built the context, as the commit gate finds it.</summary>
        private static IRequestDeadline? DeadlineOf(DbContext? context) =>
            context?.GetService<IDbContextOptions>()
                .FindExtension<CoreOptionsExtension>()?
                .ApplicationServiceProvider?
                .GetService<RequestDeadlineScope>()?
                .Deadline;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        foreach (var host in _hosts)
        {
            host.Dispose();
        }
    }
}
