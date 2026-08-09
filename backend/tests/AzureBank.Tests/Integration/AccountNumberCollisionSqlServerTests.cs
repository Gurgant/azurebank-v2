using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Both account-creation paths recover from an account-number collision (ADR-0036): registration
/// and <c>AccountService.CreateAccountAsync</c> now save through
/// <c>ConcurrencyRetry.SaveNewAccountAsync</c>, which mints a fresh number and re-saves.
///
/// <para>
/// PR #90 gave <c>TransactionNumber</c> the same treatment but narrowed it BY INDEX NAME, so it
/// deliberately did not cover <c>IX_Accounts_AccountNumber</c> — and nothing else caught
/// <c>DbUpdateException</c> on either account path.
/// </para>
/// <para>
/// <b>These tests were written against the broken behaviour first, which is why they are worth
/// keeping.</b> Measured before the fix, an injected collision on registration produced 500 to the
/// client, the ApplicationUser COMMITTED with its role assigned, ZERO accounts owned, and a 409 on
/// every retry with the same details — because the pre-checks then find the user's own row. An
/// unrecoverable, account-less user, strictly worse than the transaction case, which at least left
/// the caller free to try again. The assertions below are the inverse of each of those.
/// </para>
/// <para>
/// The third test pins the NARROWING, which is the half that rots silently: a violation on any
/// other index — the AzureTag and NormalizedEmail races that must stay the enumeration-neutral 409
/// (ADR-0013) — must not enter the retry loop. Deleting the index-name check turns it red; that was
/// verified by mutation rather than assumed, because the equivalent narrowing in #90 shipped with
/// every SQL proof green and no coverage at all.
/// </para>
/// <para>
/// SQL-gated because the whole point is the unique index, which EF InMemory ignores entirely.
/// </para>
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class AccountNumberCollisionSqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private CustomWebApplicationFactory? _factory;

    [SqlServerFact]
    public async Task RegistrationSurvivesAnAccountNumberCollision()
    {
        // A first registration, purely to put a real account number in the table to clash with.
        var (client, existingNumber) = await RegisterAsync("first");

        var collision = new DuplicateAccountNumberInterceptor(existingNumber);
        _factory!.AddInterceptor(collision);

        // The second registration's account INSERT is rewritten to reuse the first one's number,
        // so SQL Server rejects it with 2601.
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"acctcol_{unique}",
            Email = $"acctcol{unique}@example.com",
            Password = "SecurePass123!",
            FirstName = "Acct",
            LastName = "Collide",
        }, Json);

        collision.Fired.Should().BeTrue(
            "the test proves nothing if the duplicate was never actually injected");

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "a regenerable account-number clash must be recovered, not surfaced");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        /*
          The state check is the point, and it is why this ranks above the transaction case PR #90
          fixed. Registration commits the ApplicationUser in its OWN unit of work before the account
          is written, so a failed account INSERT leaves a user who holds the email and the AzureTag
          but owns no account — and cannot register again, because the pre-checks now find their own
          row and return the neutral 409. An unrecoverable, account-less user.
        */
        var user = await db.Users.SingleAsync(u => u.Email == $"acctcol{unique}@example.com");
        var accounts = await db.Accounts.Where(a => a.UserId == user.Id).ToListAsync();
        accounts.Should().ContainSingle("the registered user must end up with exactly one account");
        accounts[0].AccountNumber.Should().NotBe(existingNumber,
            "the retry must mint a FRESH number rather than reuse the one that clashed");
        accounts[0].IsPrimary.Should().BeTrue("registration's account is the primary one");
    }

    [SqlServerFact]
    public async Task CreatingAnAccountSurvivesAnAccountNumberCollision()
    {
        var (client, existingNumber) = await RegisterAsync("second");

        var collision = new DuplicateAccountNumberInterceptor(existingNumber);
        _factory!.AddInterceptor(collision);

        var response = await client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest
        {
            Name = "Savings",
            Type = Shared.Enums.AccountType.Savings,
        }, Json);

        collision.Fired.Should().BeTrue("the duplicate must actually have been injected");
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = (await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!;
        created.Id.Should().NotBeEmpty("a recovered create must still return the account it made");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var stored = await db.Accounts.SingleAsync(a => a.Id == created.Id);
        stored.AccountNumber.Should().NotBe(existingNumber,
            "the retry must mint a fresh number, not reuse the clashing one");
    }

    [SqlServerFact]
    public async Task AUniqueViolationOnADifferentIndexIsNotTreatedAsAnAccountCollision()
    {
        /*
          THE NARROWING, which is the safety-critical half. PR #90 learned this the hard way: with
          the index-name check deleted from the transaction predicate, all 24 SQL proofs stayed
          green. The same hole here would be worse — the registration path can legitimately lose the
          AzureTag or NormalizedEmail race, and that is the deliberate enumeration-neutral 409
          (ADR-0013). A predicate matching 2601/2627 alone would retry it, spinning on a genuine
          duplicate and converting a security response into a loop.

          Provoked with a REAL violation on a real DIFFERENT index rather than a fabricated
          SqlException, so the assertion holds against SQL Server's actual message text.
        */
        var (_, existingNumber) = await RegisterAsync("narrow");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var account = await db.Accounts.Include(a => a.User)
            .SingleAsync(a => a.AccountNumber == existingNumber);

        // A duplicate TransactionNumber — a genuine unique violation, on an index that is not ours.
        var shared = Shared.Utilities.IdGenerator.GenerateTransactionNumber();
        db.Transactions.Add(NewProbeTransaction(account, shared));
        await db.SaveChangesAsync();
        db.Transactions.Add(NewProbeTransaction(account, shared));

        var thrown = await ((Func<Task>)(() => db.SaveChangesAsync())).Should()
            .ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateException>();

        // Assert the provoked failure IS the intended one before drawing a conclusion from it.
        // Without this, any unrelated write error also throws DbUpdateException and also makes the
        // predicate return false, so the test would pass while proving nothing about the narrowing.
        var sql = thrown.Which.InnerException.Should()
            .BeOfType<Microsoft.Data.SqlClient.SqlException>().Subject;
        sql.Number.Should().BeOneOf(2601, 2627);
        sql.Message.Should().Contain("IX_Transactions_TransactionNumber",
            "the point is a real unique violation on a DIFFERENT index");

        Api.Services.ConcurrencyRetry.IsAccountNumberCollision(thrown.Which, attempt: 1)
            .Should().BeFalse(
                "a unique violation on any other index must propagate — retrying the registration "
                    + "duplicate would spin on a real conflict and break the neutral 409");
    }

    private static Shared.Entities.Transaction NewProbeTransaction(
        Shared.Entities.Account account, string number) => new()
    {
        Account = account,
        AccountId = account.Id,
        TransactionNumber = number,
        Type = Shared.Enums.TransactionType.Deposit,
        Amount = 1m,
        BalanceBefore = 0m,
        BalanceAfter = 1m,
        Description = "narrowing probe",
    };

    /// <summary>
    /// Registers a user and returns an authenticated client plus the number of the primary account
    /// registration opened — the value later INSERTs are made to collide with.
    /// </summary>
    private async Task<(HttpClient Client, string AccountNumber)> RegisterAsync(string prefix)
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = _factory.CreateClient();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var registration = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"{prefix}_{unique}",
            Email = $"{prefix}{unique}@example.com",
            Password = "SecurePass123!",
            FirstName = "Seed",
            LastName = "User",
        }, Json);
        registration.EnsureSuccessStatusCode();

        var registered = (await registration.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json))!.Data!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var number = await db.Accounts
            .Where(a => a.Id == registered.Account.Id)
            .Select(a => a.AccountNumber)
            .SingleAsync();

        return (client, number);
    }

    public void Dispose() => _factory?.Dispose();
}
