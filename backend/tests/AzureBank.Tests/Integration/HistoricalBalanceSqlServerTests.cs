using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The two questions that need a transaction to be OLDER than the test run: the balance "as at" a
/// past instant, and the date window on the transaction list.
///
/// <para>
/// Both were skipped as unit tests, and both skip messages misdiagnosed the cause.
/// "complex DB operations with transactions" — <c>GetBalanceAsync</c> opens no transaction. "DbContext
/// value generator overrides CreatedAt in InMemory provider" — the override is not a value generator
/// and not InMemory-specific: <c>AzureBankDbContext.UpdateTimestamps()</c> assigns
/// <c>CreatedAt = DateTime.UtcNow</c> to every Added entity on <b>every</b> provider. Seeding a
/// backdated row and calling <c>SaveChangesAsync</c> fails identically on SQL Server, so a literal
/// port of the old bodies would have produced two red tests and the appearance that the diagnosis
/// was wrong.
/// </para>
/// <para>
/// The rows therefore have to be aged OUT OF BAND, with an UPDATE that never passes through the
/// change tracker — <c>ExecuteUpdateAsync</c>. That is relational-only, which is the real and only
/// reason these two live behind <c>[SqlServerFact]</c>. A second <c>SaveChangesAsync</c> would not
/// work either: <c>EnforceTransactionImmutability</c> rejects edits to a saved transaction.
/// </para>
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class HistoricalBalanceSqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private CustomWebApplicationFactory? _factory;

    [SqlServerFact]
    public async Task GetBalance_AtAPastInstant_AnswersFromTheNewestRowAtOrBeforeIt()
    {
        var (client, accountId) = await SeedAccountAsync();

        // Two deposits, so the ledger holds rows whose BalanceAfter are 1000 and 1500. Deliberately
        // not a withdrawal: that path needs a step-up PIN, which is pure noise for a test about
        // timestamps, and a failed step-up would look like a timestamp bug.
        await DepositAsync(client, accountId, 1000m);
        await DepositAsync(client, accountId, 500m);

        var now = DateTime.UtcNow;
        await AgeTransactionsAsync(accountId, olderAt: now.AddDays(-10), newerAt: now.AddDays(-5));

        /*
          Asked as at day -7, BETWEEN the two rows. The answer must be the older row's BalanceAfter
          (1000) — not the account's current balance (500), and not the newer row's.

          That contrast is the whole point: a `GetBalanceAsync` that ignored `atTime` and returned
          the live balance would answer 500 and pass any assertion that only checked for "a number".
        */
        var atSeven = await GetBalanceAsync(client, accountId, now.AddDays(-7));
        atSeven.Balance.Should().Be(1000m, "the newest row at or before day -7 is the deposit");
        atSeven.IsHistorical.Should().BeTrue();

        // After both rows: the later one wins. Pins the ordering, which a stable-sort bug would flip.
        var atToday = await GetBalanceAsync(client, accountId, now.AddDays(-1));
        atToday.Balance.Should().Be(1500m);

        // Before either row: no history yet.
        var atStart = await GetBalanceAsync(client, accountId, now.AddDays(-20));
        atStart.Balance.Should().Be(0m, "nothing had happened to the account yet");

        // And with no instant at all, the answer is the live balance and NOT flagged historical.
        var live = await GetBalanceAsync(client, accountId, atTime: null);
        live.Balance.Should().Be(1500m);
        live.IsHistorical.Should().BeFalse();
    }

    [SqlServerFact]
    public async Task ListTransactions_FiltersByDateRange_Inclusively()
    {
        var (client, accountId) = await SeedAccountAsync();
        await DepositAsync(client, accountId, 1000m);
        await DepositAsync(client, accountId, 500m);

        var now = DateTime.UtcNow;
        var older = now.AddDays(-10);
        var newer = now.AddDays(-5);
        await AgeTransactionsAsync(accountId, olderAt: older, newerAt: newer);

        // A window that contains only the NEWER row. The original test asserted exactly this and
        // could never pass, because both rows were stamped with the run's own clock.
        var windowed = await ListAsync(client, from: now.AddDays(-8), to: now.AddDays(-3));
        windowed.Should().HaveCount(1);
        windowed[0].Amount.Should().Be(500m);

        // Both rows, when the window covers both.
        (await ListAsync(client, from: now.AddDays(-20), to: now)).Should().HaveCount(2);

        /*
          The boundaries, which the original never touched and which are the likeliest thing to
          regress: the filter is `>= From` and `<= To`, so a row landing exactly on either edge is
          INCLUDED. Swapping either for a strict comparison drops it, and no other test would notice.
        */
        var onTheEdges = await ListAsync(client, from: older, to: newer);
        onTheEdges.Should().HaveCount(2, "a row sitting exactly on From or To is inside the window");

        // A window entirely before the history returns nothing rather than everything — the failure
        // mode when a null/empty filter is treated as "no filter".
        (await ListAsync(client, from: now.AddDays(-30), to: now.AddDays(-20))).Should().BeEmpty();
    }

    /// <summary>
    /// Ages the account's two transactions out of band. <c>ExecuteUpdateAsync</c> issues a bare
    /// UPDATE, so it bypasses both <c>UpdateTimestamps</c> and the immutability guard — the only way
    /// to produce a row that is genuinely older than the test run.
    /// </summary>
    private async Task AgeTransactionsAsync(Guid accountId, DateTime olderAt, DateTime newerAt)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var ids = await db.Transactions
            .Where(t => t.AccountId == accountId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.Id)
            .ToListAsync();
        ids.Should().HaveCount(2, "the fixture seeds exactly two deposits");

        await db.Transactions.Where(t => t.Id == ids[0])
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, olderAt));
        await db.Transactions.Where(t => t.Id == ids[1])
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, newerAt));
    }

    private async Task<(HttpClient Client, Guid AccountId)> SeedAccountAsync()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = _factory.CreateClient();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"hist_{unique}",
            Email = $"hist{unique}@example.com",
            Password = "SecurePass123!",
            FirstName = "Hist",
            LastName = "Orical",
        }, Json);
        response.EnsureSuccessStatusCode();

        var registered = (await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json))!.Data!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);

        return (client, registered.Account.Id);
    }

    private static async Task DepositAsync(HttpClient client, Guid accountId, decimal amount)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(
                new DepositRequest { AccountId = accountId, Amount = amount }, options: Json),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private static async Task<BalanceResponse> GetBalanceAsync(
        HttpClient client, Guid accountId, DateTime? atTime)
    {
        var url = $"/api/accounts/{accountId}/balance";
        if (atTime is { } at)
        {
            // The query parameter is `at`, NOT `atTime` — the action parameter is named `at`
            // (AccountController.GetBalance), while the SERVICE parameter is `atTime`. Writing the
            // service's name here binds nothing, so the endpoint silently answers with the LIVE
            // balance and the test reads as a broken feature. It cost me one wrong diagnosis.
            url += $"?at={Uri.EscapeDataString(at.ToString("o"))}";
        }

        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<BalanceResponse>>(Json))!.Data!;
    }

    private static async Task<List<TransactionResponse>> ListAsync(
        HttpClient client, DateTime from, DateTime to)
    {
        var url = $"/api/transactions?FromDate={Uri.EscapeDataString(from.ToString("o"))}"
            + $"&ToDate={Uri.EscapeDataString(to.ToString("o"))}&PageSize=50";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<PaginatedResponse<TransactionResponse>>(Json);
        return page!.Data.ToList();
    }

    public void Dispose() => _factory?.Dispose();
}
