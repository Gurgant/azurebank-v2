using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Utilities;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The constraint the transaction-number widening rests on: <c>TransactionNumber</c> really is
/// unique at the database, so a generator collision is a failed INSERT rather than two rows quietly
/// sharing an identifier.
///
/// <para>
/// Worth proving on a real server because it is the one claim InMemory cannot make. EF InMemory
/// ignores <c>HasIndex(...).IsUnique()</c> entirely, so a duplicate is accepted there — every
/// existing test would stay green with the index dropped from the model, and the widened entropy
/// would be protecting nothing.
/// </para>
/// <para>
/// The generator itself is measured in <c>IdGeneratorTests</c>, which draws 20,000 numbers and
/// demands zero duplicates — a sample size chosen so it fails against the previous six-digit
/// version. This class asserts the other half: what the database does when one slips through.
/// </para>
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class TransactionNumberUniquenessSqlServerTests : IDisposable
{
    private CustomWebApplicationFactory? _factory;

    [SqlServerFact]
    public async Task DuplicateTransactionNumber_IsRejectedByTheUniqueIndex()
    {
        var accountId = await SeedAccountViaApiAsync();

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        var shared = IdGenerator.GenerateTransactionNumber();

        db.Transactions.Add(NewTransaction(account, shared, 10m));
        await db.SaveChangesAsync();

        // The same number again, on a different row. This is exactly the shape a generator
        // collision takes, minus the improbability.
        db.Transactions.Add(NewTransaction(account, shared, 20m));

        var act = async () => await db.SaveChangesAsync();
        var thrown = await act.Should().ThrowAsync<DbUpdateException>(
            "the unique index must refuse the second row rather than let two transactions share an id");

        /*
          And it is the UNIQUE violation specifically, not any write failure. 2601 is a duplicate
          key in a unique index and 2627 a PK/UNIQUE constraint — the same pair the codebase already
          keys on in IdempotencyService and UserService. Asserting the number, rather than just the
          exception type, is what stops this passing on an unrelated failure such as a timeout.
        */
        var sqlNumbers = Numbers(thrown.Which).ToList();
        sqlNumbers.Should().NotBeEmpty("the failure must come from SQL Server, not from EF's own validation");
        sqlNumbers.Should().Contain(n => n == 2601 || n == 2627);
    }

    [SqlServerFact]
    public async Task TheWidenedNumberStillFitsTheColumn()
    {
        /*
          The widening spends the one spare character the column already had: the format went from
          19 to 20 characters and `TransactionNumberLength` is 20, so no migration was needed. That
          is a comfortable claim to make and an easy one to get wrong by one, and the failure mode
          is a truncation or a String-or-binary-data-would-be-truncated error on a money write.

          So it is asserted against the real column, on the real provider, rather than against the
          constant it was derived from.
        */
        var accountId = await SeedAccountViaApiAsync();

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        var number = IdGenerator.GenerateTransactionNumber();
        number.Should().HaveLength(20);

        db.Transactions.Add(NewTransaction(account, number, 30m));
        await db.SaveChangesAsync();

        // Re-read on a fresh context: a silent truncation would surface here, not at the write.
        using var freshScope = CreateScope();
        var freshDb = freshScope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var stored = await freshDb.Transactions.SingleAsync(t => t.TransactionNumber == number);
        stored.TransactionNumber.Should().Be(number, "the column stores all 20 characters");
    }

    private static IEnumerable<int> Numbers(Exception ex)
    {
        for (var current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                foreach (SqlError error in sql.Errors)
                {
                    yield return error.Number;
                }
            }
        }
    }

    private static Transaction NewTransaction(Account account, string number, decimal amount) => new()
    {
        Account = account,
        AccountId = account.Id,
        TransactionNumber = number,
        Type = TransactionType.Deposit,
        Amount = amount,
        BalanceBefore = 0m,
        BalanceAfter = amount,
        Description = "uniqueness probe",
    };

    /// <summary>
    /// Registers a user over HTTP and returns the primary account registration opens. Going through
    /// the API rather than constructing the graph by hand is not stylistic: <c>Transaction.Account</c>
    /// and <c>Account.User</c> are `required`, so a hand-built Account needs a hand-built
    /// ApplicationUser and the test starts reimplementing registration.
    /// </summary>
    private async Task<Guid> SeedAccountViaApiAsync()
    {
        _factory ??= BuildFactory();
        var client = _factory.CreateClient();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"txnuniq_{unique}",
            Email = $"txnuniq{unique}@example.com",
            Password = "SecurePass123!",
            FirstName = "Txn",
            LastName = "Unique",
        }, JsonOptions);
        response.EnsureSuccessStatusCode();

        var registered = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions);
        return registered!.Data!.Account.Id;
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private IServiceScope CreateScope()
    {
        _factory ??= BuildFactory();
        return _factory.Services.CreateScope();
    }

    private static CustomWebApplicationFactory BuildFactory()
    {
        var factory = new CustomWebApplicationFactory();
        factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _ = factory.CreateClient(); // forces the host to build, which runs the migration
        return factory;
    }

    public void Dispose() => _factory?.Dispose();
}
