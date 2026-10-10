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
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What a deposit answers when its account is closed before the request (the control), closes
/// while the request runs, or loses its row while the request runs.
/// </summary>
/// <remarks>
/// A deposit reads its account once and reloads it only after a lost save, so its race has one
/// place: the request's own <c>UPDATE [Accounts]</c>. The closure is written there by a second
/// connection, the save loses the account's <c>RowVersion</c>, and the reload brings back the
/// closed row, or no row. Gated by AZUREBANK_TEST_SQLSERVER: the in-memory provider has no
/// <c>rowversion</c> to lose, and the closure is a raw <c>SqlConnection</c> beside the request.
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class DepositIntoClosedAccountSqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Pin = "123456";
    private const string Password = "TestPass123!";

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public DepositIntoClosedAccountSqlServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SqlServerFact]
    public async Task Control_DepositIntoAccountClosedBeforeRequest_IsRefused()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "depctl");
        var spare = await CreateSpareAsync(client, user);

        var authorizationId = await MintClosureAsync(client, user, spare);
        var deleteResponse = await DeleteAccountAsync(client, user, spare, authorizationId);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var key = Guid.NewGuid();
        var response = await SendDepositAsync(client, user.Token, spare, 5m, key.ToString());
        var body = await response.Content.ReadAsStringAsync();
        var seen = await ReadAsync(user, spare, key);

        _output.WriteLine($"status: {(int)response.StatusCode}, body: {body}, race: none, {seen}");

        using var assertions = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ErrorCodeOf(body).Should().Be(ErrorCodes.AccountNotFound);
        seen.Should().Be(new Rows(
            AccountRows: 1, IsDeleted: true, Balance: 0m, TransactionRows: 0, DepositAuditRows: 0,
            IdempotencyRecords: string.Empty, ChainIntact: true));
    }

    [SqlServerFact]
    public async Task Collision_DepositRacingAccountClosure_IsRefused_AndLeavesBalanceUnchanged()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "depcol");
        var spare = await CreateSpareAsync(client, user);

        var race = new OutOfBandClosureInterceptor(SqlServerFactAttribute.ConnectionString!, spare);
        _factory!.AddInterceptor(race);
        race.Arm();

        var key = Guid.NewGuid();
        var response = await SendDepositAsync(client, user.Token, spare, 5m, key.ToString());
        var body = await response.Content.ReadAsStringAsync();
        var sent = race.Seen;
        var afterTheRefusal = await ReadAsync(user, spare, key);

        /*
          The same key and the same bytes, sent again. The first attempt marks its record as
          executed in memory before the save that loses the race, and that mark does not reach the
          database: a record left there as executed or completed would answer this second request
          with a stored success, or with "the result is unknown, it was applied".
        */
        var again = await SendDepositAsync(client, user.Token, spare, 5m, key.ToString());
        var bodyAgain = await again.Content.ReadAsStringAsync();
        var replayed = again.Headers.Contains(IdempotencyConstants.ReplayedHeaderName);
        var afterTheSecond = await ReadAsync(user, spare, key);

        _output.WriteLine(
            $"status: {(int)response.StatusCode}, body: {body}, "
            + $"race.Fired: {race.Fired}, race.OutOfBandRowsAffected: {race.OutOfBandRowsAffected}, {sent}, "
            + $"{afterTheRefusal}, "
            + $"same key again: {(int)again.StatusCode}, replayed: {replayed}, body: {bodyAgain}, {afterTheSecond}");

        using var assertions = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, sent);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a deposit racing a closure gets the answer of an account closed before the request");
        ErrorCodeOf(body).Should().Be(ErrorCodes.AccountNotFound);

        var closedAndUntouched = new Rows(
            AccountRows: 1, IsDeleted: true, Balance: 0m, TransactionRows: 0, DepositAuditRows: 0,
            IdempotencyRecords: string.Empty, ChainIntact: true);
        afterTheRefusal.Should().Be(closedAndUntouched,
            "a closed account receives nothing, and a refusal before the commit releases the key");

        again.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the same key sent again is a new execution, refused the same way");
        ErrorCodeOf(bodyAgain).Should().Be(ErrorCodes.AccountNotFound);
        replayed.Should().BeFalse("no stored answer exists to replay, least of all a success");
        afterTheSecond.Should().Be(closedAndUntouched);
    }

    [SqlServerFact]
    public async Task Collision_DepositRacingTheRemovalOfItsAccountRow_IsRefused_AndWritesNothing()
    {
        var client = CreateSqlClient();
        var user = await RegisterWithPinAsync(client, "depgone");
        var spare = await CreateSpareAsync(client, user);

        /*
          The account has no ledger row, so nothing refers to it and the DELETE goes through. The
          save then updates no row, the reload finds none, and the entity the deposit still holds
          says an open account with its old balance: only the tracker knows the row is gone.
        */
        var race = new OutOfBandClosureInterceptor(SqlServerFactAttribute.ConnectionString!, spare, remove: true);
        _factory!.AddInterceptor(race);
        race.Arm();

        var key = Guid.NewGuid();
        var response = await SendDepositAsync(client, user.Token, spare, 5m, key.ToString());
        var body = await response.Content.ReadAsStringAsync();
        var sent = race.Seen;
        var seen = await ReadAsync(user, spare, key);

        _output.WriteLine(
            $"status: {(int)response.StatusCode}, body: {body}, "
            + $"race.Fired: {race.Fired}, race.OutOfBandRowsAffected: {race.OutOfBandRowsAffected}, {sent}, {seen}");

        using var assertions = new AssertionScope();
        ShouldHaveClosedAtTheSave(race, sent);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an account whose row is gone gets the answer of a closed one");
        ErrorCodeOf(body).Should().Be(ErrorCodes.AccountNotFound);
        seen.Should().Be(
            new Rows(
                AccountRows: 0, IsDeleted: null, Balance: null, TransactionRows: 0, DepositAuditRows: 0,
                IdempotencyRecords: string.Empty, ChainIntact: true),
            "the deposit does not write the account back, and writes nothing else");
    }

    /// <summary>
    /// The closure rides the deposit's own save, the first it sends; the deposit then reads the
    /// account again once, which only its retry does, and sends no second save.
    /// </summary>
    private static void ShouldHaveClosedAtTheSave(OutOfBandClosureInterceptor race, OutOfBandClosureInterceptor.Sent sent) =>
        ClosedAccountSqlServerProofs.ShouldHaveClosedAtTheSave(race, sent, savesAfterwards: 0, readsAfterwards: 1);

    /// <summary>What the database holds for one account and one key, as a fresh scope reads it.</summary>
    private sealed record Rows(
        int AccountRows, bool? IsDeleted, decimal? Balance, int TransactionRows, int DepositAuditRows,
        string IdempotencyRecords, bool ChainIntact)
    {
        public override string ToString() =>
            $"account rows: {AccountRows}, IsDeleted: {IsDeleted?.ToString() ?? "none"}, "
            + $"balance: {Balance?.ToString() ?? "none"}, transaction rows: {TransactionRows}, "
            + $"deposit audit rows: {DepositAuditRows}, idempotency records: [{IdempotencyRecords}], "
            + $"chain intact: {ChainIntact}";
    }

    private async Task<Rows> ReadAsync(TestUser user, Guid accountId, Guid key)
    {
        var records = await ReadIdempotencyRecordsAsync(user.UserId, key);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

        var accounts = await db.Accounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Id == accountId).ToListAsync();
        var transactionRows = await db.Transactions.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(t => t.AccountId == accountId);
        var depositAuditRows = await db.AuditEvents.AsNoTracking()
            .CountAsync(e => e.Event == SecurityEvents.MoneyDeposited && e.ActorUserId == user.UserId);
        var verification = await chain.VerifyAsync(db);

        return new Rows(
            accounts.Count, accounts.SingleOrDefault()?.IsDeleted, accounts.SingleOrDefault()?.Balance,
            transactionRows, depositAuditRows, string.Join(",", records), verification.IsIntact);
    }

    /// <summary>The status of every record the key holds for this user, read in a scope of its own.</summary>
    private async Task<List<IdempotencyStatus>> ReadIdempotencyRecordsAsync(Guid userId, Guid key)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.IdempotencyRecords.AsNoTracking()
            .Where(r => r.UserId == userId && r.Key == key)
            .Select(r => r.Status)
            .ToListAsync();
    }

    /// <summary>The body's <c>errorCode</c>, or null for a body that carries none, as a success does.</summary>
    private static string? ErrorCodeOf(string body)
    {
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty("errorCode", out var code)
            ? code.GetString()
            : null;
    }

    private sealed record TestUser(string Token, Guid UserId, Guid PrimaryAccountId);

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    private static async Task<TestUser> RegisterWithPinAsync(HttpClient client, string prefix)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"{prefix}_{unique}",
            Email = $"{prefix}{unique}@example.com",
            Password = Password,
            FirstName = "Closed",
            LastName = "Deposit"
        }, Json);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        var user = new TestUser(
            registered!.Data!.Token.AccessToken, registered.Data.User.Id, registered.Data.Account.Id);

        (await SendAsync(client, user.Token, HttpMethod.Post, "/api/auth/pin",
            new SetPinRequest { Pin = Pin, Password = Password })).EnsureSuccessStatusCode();

        return user;
    }

    private static async Task<Guid> CreateSpareAsync(HttpClient client, TestUser user)
    {
        var response = await SendAsync(client, user.Token, HttpMethod.Post, "/api/accounts",
            new CreateAccountRequest { Name = "Spare", Type = AccountType.Savings });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id;
    }

    private static async Task<Guid> MintClosureAsync(HttpClient client, TestUser user, Guid accountId)
    {
        var response = await SendAsync(client, user.Token, HttpMethod.Post,
            $"/api/accounts/{accountId}/deletion-authorizations",
            new AccountDeletionAuthorizationRequest { Pin = Pin });
        response.EnsureSuccessStatusCode();
        return (await response.Content
            .ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(Json))!.Data!.AuthorizationId;
    }

    private static async Task<HttpResponseMessage> DeleteAccountAsync(
        HttpClient client, TestUser user, Guid accountId, Guid authorizationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/accounts/{accountId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        request.Headers.Add(StepUpConstants.HeaderName, authorizationId.ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendDepositAsync(
        HttpClient client, string token, Guid accountId, decimal amount, string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(
                new DepositRequest { AccountId = accountId, Amount = amount, Description = "Proof deposit" },
                options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyConstants.HeaderName, idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsync<T>(
        HttpClient client, string token, HttpMethod method, string url, T payload)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(payload, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
