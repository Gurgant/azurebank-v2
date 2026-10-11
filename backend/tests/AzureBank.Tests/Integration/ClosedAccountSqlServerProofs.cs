using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What the proofs about a write on an account closed meanwhile, or busy on every attempt, share:
/// the users and accounts they set up through the API, the out-of-band closure, and one reading of
/// the answer and of the database, written to the test output before anything is asserted.
/// </summary>
/// <remarks>
/// The audit chain is verified over the whole shared database, not over a proof's own rows: a
/// chain broken by any class that ran earlier in the serialised collection fails every proof here.
/// </remarks>
public abstract class ClosedAccountSqlServerProofs(ITestOutputHelper output) : IDisposable
{
    protected static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    protected const string Pin = "123456";
    protected const string Password = "TestPass123!";
    protected const decimal Funding = 20m;
    protected const decimal Amount = 5m;

    /// <summary>The read of the authorisation: after the request looked at its accounts, before it reloads them.</summary>
    protected const string AuthorisationRead = "FROM [StepUpAuthorizations]";

    protected const string NoActiveAccount = "Recipient does not have an active account.";

    private CustomWebApplicationFactory? _factory;

    protected sealed record TestUser(string Token, Guid UserId, Guid PrimaryAccountId, string AzureTag);

    /// <summary>One account as the database holds it, closed or not, with its ledger rows counted.</summary>
    protected sealed record AccountRow(bool IsDeleted, decimal Balance, int TransactionRows);

    /// <summary>
    /// The answer and the database after one request. <c>Accounts</c>, <c>Primary</c> and
    /// <c>Ledgers</c> are in the order the accounts were asked for; an account whose row is gone
    /// is null in the first two and "gone" in the third.
    /// </summary>
    protected sealed record Observed(
        HttpStatusCode Status, string? ErrorCode, string? Detail, bool Replayed, string Body,
        IReadOnlyList<AccountRow?> Accounts, IReadOnlyList<bool?> Primary, IReadOnlyList<string> Ledgers,
        int MoneyAuditRows, StepUpAuthorizationStatus Authorization, DateTime? ConsumedAt,
        IReadOnlyList<IdempotencyStatus> IdempotencyRecords, bool ChainIntact, string? ChainReason,
        OutOfBandClosureInterceptor.Sent? Sent);

    protected static string NotFound(Guid accountId) => $"Account with identifier '{accountId}' was not found.";

    /// <summary>
    /// The closure rides the request's own save: one save sent before it, which loses, the accounts
    /// read again afterwards, and then as many saves as the outcome needs. The one copy: the
    /// deposit's proofs, which are not built on this class, call it too, and give the number of
    /// reads, which a deposit fixes at one.
    /// </summary>
    internal static void ShouldHaveClosedAtTheSave(
        OutOfBandClosureInterceptor race, OutOfBandClosureInterceptor.Sent sent, int savesAfterwards,
        int rowsWritten = 1, int? readsAfterwards = null)
    {
        race.Fired.Should().BeTrue("the out-of-band closure must actually have run");
        race.OutOfBandRowsAffected.Should().Be(rowsWritten, "and it must have met an account that was still open");
        sent.RodeAnAccountUpdate.Should().BeTrue("the closure lands on the request's own save");
        sent.AccountUpdatesBefore.Should().Be(1, "that save is the first the request sends");
        sent.AccountReadsAfter.Should().BeGreaterThan(
            0, "a save that loses sends the request round its retry, which reads the accounts again");
        if (readsAfterwards is { } reads)
        {
            sent.AccountReadsAfter.Should().Be(reads);
        }

        sent.AccountUpdatesAfter.Should().Be(savesAfterwards);
    }

    /// <summary>
    /// The closure lands after the request read its accounts and before it saves anything: it
    /// rides no save, so no save loses and nothing goes round a retry because of it. The reads of
    /// accounts after it are counted exactly: the reload of each account of the attempt, the reads
    /// of a choice where one is made, and what the request reads of accounts on its way to the
    /// save. One read more is one look more, and a lost save would add a reload of each account.
    /// </summary>
    protected static void ShouldHaveClosedBeforeTheFirstReload(
        OutOfBandClosureInterceptor race, Observed seen, int readsAfterwards, int savesAfterwards,
        int rowsWritten = 1)
    {
        race.Fired.Should().BeTrue("the out-of-band closure must actually have run");
        race.OutOfBandRowsAffected.Should().Be(rowsWritten, "and it must have met an account that was still open");
        seen.Sent!.RodeAnAccountUpdate.Should().BeFalse("the closure rides a read, not a save");
        seen.Sent.AccountReadsBefore.Should().BeGreaterThan(0, "the request looks at its accounts before the closure");
        seen.Sent.AccountUpdatesBefore.Should().Be(0, "and saves nothing before it");
        seen.Sent.AccountReadsAfter.Should().Be(readsAfterwards, "the accounts are read again after it, this many times");
        seen.Sent.AccountUpdatesAfter.Should().Be(savesAfterwards);
    }

    protected static void ShouldBeRefused(Observed seen, HttpStatusCode status, string errorCode, string detail)
    {
        seen.Status.Should().Be(status, "a closed account gets one answer, whenever it closed");
        seen.ErrorCode.Should().Be(errorCode);
        seen.Detail.Should().Be(detail);
        seen.Replayed.Should().BeFalse();
    }

    protected static void ShouldHaveMovedNothing(Observed seen, params AccountRow?[] accounts)
    {
        seen.Accounts.Should().Equal(accounts, "no balance moves and no ledger row is written");
        seen.MoneyAuditRows.Should().Be(0, "a movement that did not happen leaves no money audit row");
        seen.Authorization.Should().Be(StepUpAuthorizationStatus.Pending, "a refusal spends no authorisation");
        seen.ConsumedAt.Should().BeNull();
        seen.IdempotencyRecords.Should().BeEmpty("a refusal before the commit releases the key");
        seen.ChainIntact.Should().BeTrue(because: seen.ChainReason);
    }

    /// <summary>
    /// The answer of a write whose account stayed busy: 503 <c>SERVICE_UNAVAILABLE</c>, never
    /// cached, a wait of one second in the body and in the <c>Retry-After</c> header. A write that
    /// holds an idempotency key also says <c>applied: false</c>, with the sentence that says
    /// nothing was changed; a write that holds none says nothing of <c>applied</c>.
    /// </summary>
    internal static void ShouldBeTheBusy503(HttpResponseMessage response, string body, bool saysNothingWasApplied)
    {
        response.StatusCode.Should().Be(
            HttpStatusCode.ServiceUnavailable,
            $"a write that was not made is answered the 503 a client can retry; the body was: {body}");
        (response.Headers.CacheControl?.NoStore).Should().BeTrue("a 503 describes a moment and must not be cached");
        (response.Headers.RetryAfter?.Delta).Should().Be(
            TimeSpan.FromSeconds(1), "the Retry-After header carries the wait the body names");

        var problem = JsonSerializer.Deserialize<JsonElement>(body);
        problem.GetProperty("status").GetInt32().Should().Be(503);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.ServiceUnavailable);
        problem.GetProperty("retryAfterSeconds").GetInt32().Should().Be(
            1, "a busy account names its own wait, not the ten seconds of an outage");

        if (saysNothingWasApplied)
        {
            problem.TryGetProperty("applied", out var applied).Should().BeTrue(
                $"nothing was written, and the answer says so; the body was: {body}");
            applied.ValueKind.Should().Be(JsonValueKind.False);
            problem.GetProperty("detail").GetString().Should().Be(
                AzureBank.Api.Handlers.ServiceUnavailableExceptionHandler.BusyNothingAppliedDetail);
        }
        else
        {
            problem.TryGetProperty("applied", out _).Should().BeFalse(
                $"a write with no idempotency claim has nothing to say applied on; the body was: {body}");
            problem.GetProperty("detail").GetString().Should().Be(
                AzureBank.Api.Handlers.ServiceUnavailableExceptionHandler.BusyDetail);
        }
    }

    /// <summary>
    /// What another request does to an authorisation: spends it, or, for one that expires, moves
    /// its expiry into the past. On a connection of its own; returns the rows it matched.
    /// </summary>
    protected static async Task<int> SpendOrExpireAsync(Guid authorizationId, bool expires, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(SqlServerFactAttribute.ConnectionString!);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = expires
            ? "UPDATE [StepUpAuthorizations] SET [ExpiresAt] = DATEADD(second, -1, SYSUTCDATETIME()) "
                + "WHERE [Id] = @id AND [Status] = 'Pending'"
            : "UPDATE [StepUpAuthorizations] SET [Status] = 'Consumed', [ConsumedAt] = SYSUTCDATETIME() "
                + "WHERE [Id] = @id AND [Status] = 'Pending'";
        command.Parameters.Add(new SqlParameter("@id", authorizationId));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the answer and then the database, in a scope of its own so nothing comes from a
    /// tracker, and writes both to the test output before anything is asserted.
    /// </summary>
    protected async Task<Observed> ObserveAsync(
        HttpResponseMessage response, TestUser actor, string moneyEvent, Guid authorizationId,
        Guid idempotencyKey, OutOfBandClosureInterceptor? race, params Guid[] accountIds)
    {
        var sent = race?.Seen;
        var body = await response.Content.ReadAsStringAsync();
        string? errorCode = null;
        string? detail = null;
        if (!response.IsSuccessStatusCode)
        {
            var problem = JsonSerializer.Deserialize<JsonElement>(body);
            errorCode = problem.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
            detail = problem.TryGetProperty("detail", out var text) ? text.GetString() : null;
        }

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

        var accounts = new List<AccountRow?>();
        var primary = new List<bool?>();
        var ledgers = new List<string>();
        foreach (var accountId in accountIds)
        {
            var account = await db.Accounts.IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(a => a.Id == accountId);
            var types = await db.Transactions.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.AccountId == accountId)
                .OrderBy(t => t.CreatedAt).ThenBy(t => t.Type)
                .Select(t => t.Type)
                .ToListAsync();
            accounts.Add(account is null ? null : new AccountRow(account.IsDeleted, account.Balance, types.Count));
            primary.Add(account?.IsPrimary);
            ledgers.Add(account is null ? "gone" : string.Join("+", types));
        }

        var moneyAuditRows = await db.AuditEvents.AsNoTracking()
            .CountAsync(e => e.Event == moneyEvent && e.ActorUserId == actor.UserId);
        var authorization = await db.StepUpAuthorizations.AsNoTracking().SingleAsync(a => a.Id == authorizationId);
        var idempotencyRecords = await db.IdempotencyRecords.AsNoTracking()
            .Where(r => r.UserId == actor.UserId && r.Key == idempotencyKey)
            .Select(r => r.Status)
            .ToListAsync();
        var verification = await chain.VerifyAsync(db);

        var seen = new Observed(
            response.StatusCode, errorCode, detail,
            response.Headers.Contains(IdempotencyConstants.ReplayedHeaderName), body,
            accounts, primary, ledgers, moneyAuditRows, authorization.Status, authorization.ConsumedAt,
            idempotencyRecords, verification.IsIntact, verification.Reason, sent);

        output.WriteLine(
            $"status: {(int)seen.Status}, errorCode: {seen.ErrorCode ?? "none"}, replayed: {seen.Replayed}, "
            + (race is null
                ? "race: none, "
                : $"race.Fired: {race.Fired}, race.OutOfBandRowsAffected: {race.OutOfBandRowsAffected}, {sent}, ")
            + "accounts: "
            + string.Join(" ", accounts.Select((a, i) => a is null
                ? "[gone]"
                : $"[IsDeleted: {a.IsDeleted}, primary: {primary[i]}, balance: {a.Balance}, "
                    + $"transaction rows: {a.TransactionRows} ({ledgers[i]})]"))
            + $", {moneyEvent} audit rows: {seen.MoneyAuditRows}, authorisation: {seen.Authorization}, "
            + $"consumed at set: {seen.ConsumedAt is not null}, "
            + $"idempotency records: [{string.Join(",", seen.IdempotencyRecords)}], chain intact: {seen.ChainIntact}, "
            + $"body: {body}");

        return seen;
    }

    /// <summary>
    /// A host on the test database. With a fault it also retries transient failures, as production
    /// does, and carries the two interceptors that inject the fault.
    /// </summary>
    protected HttpClient CreateSqlClient(TransferTransientFault? fault = null)
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        if (fault is not null)
        {
            _factory.EnableSqlRetryOnFailure();
            _factory.AddInterceptor(new TransferCommandFaultInterceptor(fault));
            _factory.AddInterceptor(new TransferCommitFaultInterceptor(fault));
        }

        return _factory.CreateClient();
    }

    /// <summary>A host on the test database, set up by the proof before it starts.</summary>
    protected HttpClient CreateSqlClient(Action<CustomWebApplicationFactory> configure)
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        configure(_factory);
        return _factory.CreateClient();
    }

    /// <summary>The host the proof runs on, for what a proof reads from it: its services and its log.</summary>
    protected CustomWebApplicationFactory Host => _factory!;

    /// <summary>One more interceptor on every context the host builds from now on.</summary>
    protected void AddInterceptor(Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor interceptor) =>
        _factory!.AddInterceptor(interceptor);

    /// <summary>Registered now and armed now, so every write of the setup is behind it.</summary>
    protected OutOfBandClosureInterceptor ArmClosureOf(
        Guid accountId, string trigger = OutOfBandClosureInterceptor.AccountUpdate,
        bool emptyFirst = false, Guid? newPrimaryId = null, bool remove = false, Guid? onTheReadOf = null,
        bool keepOpen = false)
    {
        var race = new OutOfBandClosureInterceptor(
            SqlServerFactAttribute.ConnectionString!, accountId, trigger, emptyFirst, newPrimaryId, remove,
            onTheReadOf, keepOpen);
        _factory!.AddInterceptor(race);
        race.Arm();
        return race;
    }

    protected static async Task<TestUser> RegisterAsync(HttpClient client, string prefix)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var tag = $"{prefix}_{unique}";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = tag,
            Email = $"{prefix}{unique}@example.com",
            Password = Password,
            FirstName = "Closed",
            LastName = "Account"
        }, Json);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        return new TestUser(
            registered!.Data!.Token.AccessToken, registered.Data.User.Id, registered.Data.Account.Id, tag);
    }

    protected static async Task<TestUser> RegisterWithPinAsync(HttpClient client, string prefix)
    {
        var user = await RegisterAsync(client, prefix);
        (await SendAsync(client, user, HttpMethod.Post, "/api/auth/pin",
            new SetPinRequest { Pin = Pin, Password = Password })).EnsureSuccessStatusCode();
        return user;
    }

    protected static async Task<Guid> CreateSpareAsync(HttpClient client, TestUser user)
    {
        var response = await SendAsync(client, user, HttpMethod.Post, "/api/accounts",
            new CreateAccountRequest { Name = "Spare", Type = AccountType.Savings });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id;
    }

    /// <summary>The bank's own change of the primary account.</summary>
    protected static async Task MakePrimaryThroughTheApiAsync(HttpClient client, TestUser user, Guid accountId)
    {
        var response = await SendAsync<object?>(
            client, user, HttpMethod.Patch, $"/api/accounts/{accountId}/set-primary", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the control's account must really be the primary one");
    }

    protected static async Task FundAsync(HttpClient client, TestUser user, Guid accountId)
    {
        (await SendAsync(client, user, HttpMethod.Post, "/api/transactions/deposit",
            new DepositRequest { AccountId = accountId, Amount = Funding, Description = "funding" },
            idempotencyKey: Guid.NewGuid())).EnsureSuccessStatusCode();
    }

    /// <summary>The bank's own closure: the PIN mints an authorisation and the DELETE spends it.</summary>
    protected static async Task CloseThroughTheApiAsync(HttpClient client, TestUser user, Guid accountId)
    {
        var authorizationId = await MintAsync(
            client, user, $"/api/accounts/{accountId}/deletion-authorizations",
            new AccountDeletionAuthorizationRequest { Pin = Pin });
        var response = await SendAsync<object?>(
            client, user, HttpMethod.Delete, $"/api/accounts/{accountId}", null, authorizationId);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the control's account must really be closed");
    }

    protected static Task<Guid> MintTransferAsync(
        HttpClient client, TestUser user, Guid fromAccountId, string recipientTag, decimal amount = Amount) =>
        MintAsync(client, user, "/api/transfers/authorizations", new TransferAuthorizationRequest
        {
            FromAccountId = fromAccountId,
            RecipientAzureTag = recipientTag,
            Amount = amount,
            Pin = Pin
        });

    protected static Task<Guid> MintInternalAsync(HttpClient client, TestUser user, Guid fromAccountId, Guid toAccountId) =>
        MintAsync(client, user, "/api/transfers/internal/authorizations", new InternalTransferAuthorizationRequest
        {
            FromAccountId = fromAccountId,
            ToAccountId = toAccountId,
            Amount = Amount,
            Pin = Pin
        });

    protected static Task<Guid> MintWithdrawalAsync(
        HttpClient client, TestUser user, Guid accountId, decimal amount = Amount) =>
        MintAsync(client, user, "/api/transactions/withdraw/authorizations",
            new WithdrawalAuthorizationRequest { AccountId = accountId, Amount = amount, Pin = Pin });

    protected static async Task<Guid> MintAsync<T>(HttpClient client, TestUser user, string url, T payload)
    {
        var response = await SendAsync(client, user, HttpMethod.Post, url, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content
            .ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(Json))!.Data!.AuthorizationId;
    }

    protected static Task<HttpResponseMessage> TransferAsync(
        HttpClient client, TestUser user, Guid fromAccountId, string recipientTag,
        Guid authorizationId, Guid idempotencyKey, decimal amount = Amount) =>
        SendAsync(client, user, HttpMethod.Post, "/api/transfers", new TransferRequest
        {
            FromAccountId = fromAccountId,
            RecipientAzureTag = recipientTag,
            Amount = amount,
            Description = "closed account proof"
        }, authorizationId, idempotencyKey);

    protected static Task<HttpResponseMessage> InternalTransferAsync(
        HttpClient client, TestUser user, Guid fromAccountId, Guid toAccountId,
        Guid authorizationId, Guid idempotencyKey) =>
        SendAsync(client, user, HttpMethod.Post, "/api/transfers/internal", new InternalTransferRequest
        {
            FromAccountId = fromAccountId,
            ToAccountId = toAccountId,
            Amount = Amount,
            Description = "closed account proof"
        }, authorizationId, idempotencyKey);

    protected static Task<HttpResponseMessage> WithdrawAsync(
        HttpClient client, TestUser user, Guid accountId, Guid authorizationId, Guid idempotencyKey,
        decimal amount = Amount) =>
        SendAsync(client, user, HttpMethod.Post, "/api/transactions/withdraw", new WithdrawRequest
        {
            AccountId = accountId,
            Amount = amount,
            Description = "closed account proof"
        }, authorizationId, idempotencyKey);

    protected static async Task<HttpResponseMessage> SendAsync<T>(
        HttpClient client, TestUser user, HttpMethod method, string url, T payload,
        Guid? authorizationId = null, Guid? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: Json);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        if (authorizationId is { } authorization)
        {
            request.Headers.Add(StepUpConstants.HeaderName, authorization.ToString());
        }

        if (idempotencyKey is { } key)
        {
            request.Headers.Add(IdempotencyConstants.HeaderName, key.ToString());
        }

        return await client.SendAsync(request);
    }

    public void Dispose()
    {
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }
}
