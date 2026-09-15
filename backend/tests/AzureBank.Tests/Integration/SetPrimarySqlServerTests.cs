using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Making a second account primary, on the provider that enforces "one primary per user".
/// </summary>
/// <remarks>
/// <para>
/// <c>UX_Accounts_UserId_Primary</c> is a filtered unique index (<c>IsPrimary = 1 AND IsDeleted = 0</c>).
/// <c>SetPrimaryAccountAsync</c> cleared the old primary and set the new one in ONE SaveChanges,
/// and SQL Server ran the new row's UPDATE first. Found by Schemathesis on 2026-09-15, the first
/// run of the conformance gate: <c>PATCH /api/accounts/{id}/set-primary</c> answered 500,
/// "Cannot insert duplicate key row in object 'dbo.Accounts' with unique index
/// 'UX_Accounts_UserId_Primary'. The duplicate key value is (&lt;userId&gt;, 1)." The InMemory
/// provider enforces no such index, which is why <c>AccountEndpointTests</c> has passed this
/// scenario since the endpoint existed; this lives behind <c>[SqlServerFact]</c> for that reason.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class SetPrimarySqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private CustomWebApplicationFactory? _factory;

    [SqlServerFact]
    public async Task ASecondAccountBecomesPrimary_AndTheFirstStopsBeingIt()
    {
        var (client, first) = await SeedAccountAsync();
        var created = await client.PostAsJsonAsync(
            "/api/accounts", new CreateAccountRequest { Name = "Savings", Type = AccountType.Savings }, Json);
        created.EnsureSuccessStatusCode();
        var second = (await created.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id;

        var response = await client.PatchAsync($"/api/accounts/{second}/set-primary", null);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "one primary per user is what the index enforces, and the service has to clear the old one "
            + "before it sets the new one: {0}", await response.Content.ReadAsStringAsync());
        (await client.GetFromJsonAsync<ApiResponse<AccountResponse>>($"/api/accounts/{second}", Json))!
            .Data!.IsPrimary.Should().BeTrue();
        (await client.GetFromJsonAsync<ApiResponse<AccountResponse>>($"/api/accounts/{first}", Json))!
            .Data!.IsPrimary.Should().BeFalse("the old primary was cleared in the same act");
    }

    /// <summary>
    /// Swaps racing for the same user all succeed, and exactly one account is primary afterwards.
    /// </summary>
    /// <remarks>
    /// Found in review on 2026-09-15: the service loaded the target and the current primary BEFORE
    /// its transaction, so two concurrent swaps each cleared a primary the other had already moved.
    /// Measured red before the fix: the loser threw DbUpdateConcurrencyException ("expected to affect
    /// 1 row(s), but actually affected 0") and answered 500, because the execution strategy retries
    /// transient faults only. Each round fires every account at once, twice over, so every request
    /// races at least one other swap.
    /// </remarks>
    [SqlServerFact]
    public async Task ConcurrentSwapsForOneUser_AllSucceed_AndExactlyOneAccountEndsPrimary()
    {
        var (client, first) = await SeedAccountAsync();
        var accounts = new List<Guid> { first };
        for (var i = 0; i < 5; i++)
        {
            var created = await client.PostAsJsonAsync(
                "/api/accounts", new CreateAccountRequest { Name = $"Extra {i}", Type = AccountType.Savings }, Json);
            created.EnsureSuccessStatusCode();
            accounts.Add((await created.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id);
        }

        var failures = new List<string>();
        for (var round = 0; round < 4; round++)
        {
            var responses = await Task.WhenAll(
                accounts.Concat(accounts).Select(id => client.PatchAsync($"/api/accounts/{id}/set-primary", null)));
            foreach (var response in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
            {
                failures.Add($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }
        }

        failures.Should().BeEmpty("a swap that loses a race with another swap for the same user runs again");
        var listed = (await client.GetFromJsonAsync<ApiResponse<List<AccountResponse>>>("/api/accounts", Json))!.Data!;
        listed.Count(a => a.IsPrimary).Should().Be(1, "the index allows one primary per user, and the service must leave one");
    }

    /// <summary>
    /// A swap racing deposits on the same accounts reloads and runs again instead of answering 500.
    /// </summary>
    /// <remarks>
    /// Deposits do not take the swap's lock, and each one moves the account's RowVersion, so a swap
    /// that read an account before a deposit landed saves against a stale token. The bounded retry in
    /// <c>SetPrimaryAccountAsync</c> is what absorbs it. Measured 2026-09-15 as the positive control:
    /// with that catch disabled this test answered 500 in five runs of five, and passed five of five
    /// with it (six rounds caught it only two runs in three, hence fifteen). Balances are asserted
    /// too, so a retry that replayed a deposit, or a swap that lost one, cannot pass.
    /// </remarks>
    [SqlServerFact]
    public async Task SwapsRacingDepositsOnTheSameAccounts_AllSucceed_AndNoDepositIsLost()
    {
        var (client, first) = await SeedAccountAsync();
        var accounts = new List<Guid> { first };
        for (var i = 0; i < 3; i++)
        {
            var created = await client.PostAsJsonAsync(
                "/api/accounts", new CreateAccountRequest { Name = $"Race {i}", Type = AccountType.Savings }, Json);
            created.EnsureSuccessStatusCode();
            accounts.Add((await created.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id);
        }

        const int rounds = 15;
        const decimal amount = 10m;
        var failures = new List<string>();
        for (var round = 0; round < rounds; round++)
        {
            var work = accounts.SelectMany(id => new Func<Task<HttpResponseMessage>>[]
            {
                () => client.PatchAsync($"/api/accounts/{id}/set-primary", null),
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
                    {
                        Content = JsonContent.Create(new DepositRequest { AccountId = id, Amount = amount }, options: Json),
                    };
                    request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
                    return client.SendAsync(request);
                },
            });
            var responses = await Task.WhenAll(work.Select(start => start()));
            foreach (var response in responses.Where(r => !r.IsSuccessStatusCode))
            {
                failures.Add($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }
        }

        failures.Should().BeEmpty("a RowVersion moved by a deposit is retried by the swap, not surfaced");
        var listed = (await client.GetFromJsonAsync<ApiResponse<List<AccountResponse>>>("/api/accounts", Json))!.Data!;
        listed.Count(a => a.IsPrimary).Should().Be(1);
        listed.Sum(a => a.Balance).Should().Be(
            amount * rounds * accounts.Count, "every deposit landed exactly once while the swaps ran");
    }

    private async Task<(HttpClient Client, Guid AccountId)> SeedAccountAsync()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = _factory.CreateClient();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"prim_{unique}",
            Email = $"prim{unique}@example.com",
            Password = "SecurePass123!",
            FirstName = "Set",
            LastName = "Primary",
        }, Json);
        response.EnsureSuccessStatusCode();
        var registered = (await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json))!.Data!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
        return (client, registered.Account.Id);
    }

    public void Dispose() => _factory?.Dispose();
}
