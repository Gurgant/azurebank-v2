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
/// Proves what a deposit answers when an account is closed before the request or during its execution.
/// </summary>
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

        var response = await SendDepositAsync(client, user.Token, spare, 5m, Guid.NewGuid().ToString());
        var body = await response.Content.ReadAsStringAsync();

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var account = await db.Accounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == spare);
        var transactionRows = await db.Transactions.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(t => t.AccountId == spare);
        var depositAuditRows = await db.AuditEvents.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(e => e.Event == SecurityEvents.MoneyDeposited && e.ActorUserId == user.UserId);

        _output.WriteLine(
            $"status: {(int)response.StatusCode}, body: {body}, "
            + $"race.Fired: False, race.OutOfBandRowsAffected: 0, "
            + $"IsDeleted: {account.IsDeleted}, balance: {account.Balance}, "
            + $"transaction rows: {transactionRows}, deposit audit rows: {depositAuditRows}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = JsonSerializer.Deserialize<JsonElement>(body);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.AccountNotFound);

        account.IsDeleted.Should().BeTrue();
        account.Balance.Should().Be(0m);
        transactionRows.Should().Be(0);
        depositAuditRows.Should().Be(0);
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
        var recordsAfterTheRefusal = await ReadIdempotencyRecordsAsync(user.UserId, key);

        /*
          The same key and the same bytes, sent again. The first attempt marked its record as
          executed in memory before the save that lost the race, and that mark must not have
          reached the database: a record left there as executed or completed would answer this
          second request with a stored success, or with "the result is unknown, it was applied".
        */
        var again = await SendDepositAsync(client, user.Token, spare, 5m, key.ToString());
        var bodyAgain = await again.Content.ReadAsStringAsync();
        var replayed = again.Headers.Contains(IdempotencyConstants.ReplayedHeaderName);
        var recordsAfterTheSecond = await ReadIdempotencyRecordsAsync(user.UserId, key);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var account = await db.Accounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == spare);
        var transactionRows = await db.Transactions.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(t => t.AccountId == spare);
        var depositAuditRows = await db.AuditEvents.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(e => e.Event == SecurityEvents.MoneyDeposited && e.ActorUserId == user.UserId);

        _output.WriteLine(
            $"status: {(int)response.StatusCode}, body: {body}, "
            + $"race.Fired: {race.Fired}, race.OutOfBandRowsAffected: {race.OutOfBandRowsAffected}, "
            + $"idempotency records after the refusal: [{string.Join(",", recordsAfterTheRefusal)}], "
            + $"same key again: {(int)again.StatusCode}, replayed: {replayed}, body: {bodyAgain}, "
            + $"idempotency records after it: [{string.Join(",", recordsAfterTheSecond)}], "
            + $"IsDeleted: {account.IsDeleted}, balance: {account.Balance}, "
            + $"transaction rows: {transactionRows}, deposit audit rows: {depositAuditRows}");

        using var assertions = new AssertionScope();

        race.Fired.Should().BeTrue("the out-of-band closure must actually have run");
        race.OutOfBandRowsAffected.Should().Be(1, "and it must have closed the open account");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a deposit racing a closure must receive the same refusal as a closed account");
        ErrorCodeOf(body).Should().Be(ErrorCodes.AccountNotFound);
        recordsAfterTheRefusal.Should().BeEmpty(
            "a refusal before the commit releases the key and leaves no record that could answer for it");

        again.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the same key sent again is a new execution, refused the same way");
        ErrorCodeOf(bodyAgain).Should().Be(ErrorCodes.AccountNotFound);
        replayed.Should().BeFalse("no stored answer exists to replay, least of all a success");
        recordsAfterTheSecond.Should().BeEmpty();

        account.IsDeleted.Should().BeTrue("the account was closed by the racing update");
        account.Balance.Should().Be(0m, "a closed account must not receive deposits");
        transactionRows.Should().Be(0, "no transaction row may be written for a closed account");
        depositAuditRows.Should().Be(0, "no deposit audit row may be written for a closed account");
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
