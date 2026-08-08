using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Api.Services;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A transaction-number collision is RECOVERED, not surfaced.
///
/// <para>
/// Before this, a duplicate generated number violated <c>IX_Transactions_TransactionNumber</c> and
/// escaped as a raw 500 on a money endpoint, with the idempotency record stranded mid-flight —
/// nothing on the deposit, withdraw or transfer paths caught <c>DbUpdateException</c>. Widening the
/// suffix (PR #89) made that astronomically unlikely; it did not make it impossible, and "unlikely"
/// is not a recovery strategy.
/// </para>
/// <para>
/// The collision is INJECTED, because 32⁷ values per day means waiting for a natural one is not a
/// test strategy — and an unreachable recovery path is indistinguishable from a broken one. What is
/// injected is only the duplicate VALUE: SQL Server raises the genuine 2601 against the genuine
/// index, so the production predicate (error number AND index name) is exercised for real.
/// </para>
/// <para>
/// SQL-gated because the whole point is the unique index, which EF InMemory ignores entirely.
/// </para>
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class TransactionNumberCollisionRecoverySqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private CustomWebApplicationFactory? _factory;

    [SqlServerFact]
    public async Task ADepositWhoseNumberCollides_RegeneratesAndSucceeds()
    {
        // A first deposit, purely to put a real number in the table for the second one to clash with.
        var (client, accountId, existingNumber) = await SeedFirstDepositAsync();

        var collision = new DuplicateTransactionNumberInterceptor(existingNumber);
        _factory!.AddInterceptor(collision);

        // The second deposit's INSERT is rewritten to reuse the first one's number, so SQL Server
        // rejects it with 2601. The retry loop must mint a fresh number and commit.
        var response = await DepositAsync(client, accountId, 250m);

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "the collision must be recovered, not surfaced as a 500");
        collision.Fired.Should().BeTrue(
            "the test proves nothing if the duplicate was never actually injected");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        // Both deposits exist, with DIFFERENT numbers — the retry regenerated rather than
        // overwriting or dropping a row.
        var numbers = await db.Transactions
            .Where(t => t.AccountId == accountId)
            .Select(t => t.TransactionNumber)
            .ToListAsync();
        numbers.Should().HaveCount(2);
        numbers.Distinct().Should().HaveCount(2);
        numbers.Should().Contain(existingNumber);

        // And the money is right exactly once: 100 + 250. A retry that re-applied the failed
        // attempt's balance mutation would read 600 here — which is the failure mode
        // ConcurrencyRetry.ResetToStoreAsync exists to prevent, now exercised on this path too.
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        account.Balance.Should().Be(350m, "the debit/credit must be applied once, not twice");
    }

    [SqlServerFact]
    public async Task AUniqueViolationOnADIFFERENTIndex_IsNotTreatedAsACollision()
    {
        /*
          The narrowing, which is the safety-critical half and was untested until a mutation showed
          it: deleting the index-name check from the predicate left all 24 SQL proofs green.

          2601/2627 also carry the IDEMPOTENCY CLAIM race, and that claim is a distributed lock —
          retrying it would double-execute the very operation it exists to make exactly-once. So the
          predicate must match the transaction-number index and nothing else.

          Provoked with a REAL violation on a real different index (AccountNumber) rather than a
          fabricated SqlException, so the assertion holds against SQL Server's actual message text
          rather than against my idea of it.
        */
        var (_, accountId, _) = await SeedFirstDepositAsync();

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        // Include(User): Account.User is a REQUIRED navigation, so it cannot simply be left out —
        // the compiler rejects that — and assigning a null one would make SaveChanges fail before
        // SQL Server ever reaches the duplicate AccountNumber. Loading it keeps the duplicate the
        // only reason this write can fail.
        var account = await db.Accounts.Include(a => a.User).SingleAsync(a => a.Id == accountId);

        var clash = new AzureBank.Shared.Entities.Account
        {
            UserId = account.UserId,
            AccountNumber = account.AccountNumber, // duplicate: violates IX_Accounts_AccountNumber
            Name = "Clash",
            Type = account.Type,
            Balance = 0m,
            IsPrimary = false,
            User = account.User,
        };
        db.Accounts.Add(clash);

        var thrown = await ((Func<Task>)(() => db.SaveChangesAsync())).Should()
            .ThrowAsync<DbUpdateException>();

        /*
          Assert the provoked failure IS the intended one before drawing a conclusion from it.
          Without this, any unrelated write error — a null required navigation, a length violation —
          also throws DbUpdateException and also makes the predicate return false, so the test would
          pass while proving nothing about the narrowing it exists to pin. That is the same
          wrong-reason pass this suite has now been bitten by three times.
        */
        var sql = thrown.Which.InnerException.Should().BeOfType<SqlException>().Subject;
        sql.Number.Should().BeOneOf(2601, 2627);
        sql.Message.Should().Contain("IX_Accounts_AccountNumber",
            "the point is a real unique violation on a DIFFERENT index");

        ConcurrencyRetry.IsTransactionNumberCollision(thrown.Which, attempt: 1).Should().BeFalse(
            "a unique violation on any OTHER index must propagate — retrying the idempotency "
                + "claim race would double-execute the operation it protects");
    }

    private async Task<(HttpClient Client, Guid AccountId, string Number)> SeedFirstDepositAsync()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = _factory.CreateClient();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var registration = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"collide_{unique}",
            Email = $"collide{unique}@example.com",
            Password = "SecurePass123!",
            FirstName = "Coll",
            LastName = "Ision",
        }, Json);
        registration.EnsureSuccessStatusCode();

        var registered = (await registration.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json))!.Data!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);

        var first = await DepositAsync(client, registered.Account.Id, 100m);
        first.EnsureSuccessStatusCode();
        var deposit = (await first.Content.ReadFromJsonAsync<ApiResponse<DepositResponse>>(Json))!.Data!;

        return (client, registered.Account.Id, deposit.Transaction.TransactionNumber);
    }

    private static async Task<HttpResponseMessage> DepositAsync(
        HttpClient client, Guid accountId, decimal amount)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(
                new DepositRequest { AccountId = accountId, Amount = amount }, options: Json),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }

    public void Dispose() => _factory?.Dispose();
}
