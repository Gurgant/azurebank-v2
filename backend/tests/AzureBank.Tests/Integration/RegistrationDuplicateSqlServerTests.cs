using AzureBank.Api.Services;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The two index names <c>ConcurrencyRetry.IsRegistrationDuplicate</c> matches on are the real ones.
///
/// <para>
/// This exists because a typo in either constant fails SILENTLY in the worst direction: a genuine
/// write-time race loser stops being the enumeration-neutral 409 of ADR-0013 and becomes a 500.
/// Nothing else would notice — the names are strings, compared against a message, and the
/// registration paths that use them are exercised by tests that never provoke a real violation.
/// </para>
/// <para>
/// So the violations here are REAL, raised by SQL Server against the real indexes, and each is
/// asserted to be the intended one (error number AND index name) BEFORE any conclusion is drawn
/// from it. Without that, an unrelated write failure would also throw <c>DbUpdateException</c>, also
/// make the predicate return false, and the test would pass while proving nothing.
/// </para>
/// <para>
/// The names themselves come from the migrations, not from the model: <c>EmailIndex</c> is
/// Identity's own, made unique and NULL-filtered by <c>AddUniqueEmailIndex</c> — it is NOT called
/// "NormalizedEmail", which is the column — and <c>IX_AspNetUsers_AzureTag</c> is EF's convention
/// name from <c>InitialCreate</c>.
/// </para>
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class RegistrationDuplicateSqlServerTests : IDisposable
{
    private CustomWebApplicationFactory? _factory;

    /// <summary>
    /// Held, not <c>using</c>-scoped: the DbContext it resolves has to outlive
    /// <c>SeedUserAsync</c> and stay usable for the whole test, so the scope is disposed in
    /// <see cref="Dispose"/> instead. Leaking it would hold a context and its SQL connection
    /// open per test.
    /// </summary>
    private IServiceScope? _scope;

    [SqlServerFact]
    public async Task ADuplicateAzureTagIsClassifiedAsARegistrationDuplicate()
    {
        var (db, existing) = await SeedUserAsync();

        db.Users.Add(NewUser(azureTag: existing.AzureTag, email: $"other{Unique()}@example.com"));

        var thrown = await ((Func<Task>)(() => db.SaveChangesAsync())).Should()
            .ThrowAsync<DbUpdateException>();

        var sql = thrown.Which.InnerException.Should().BeOfType<SqlException>().Subject;
        sql.Number.Should().BeOneOf(2601, 2627);
        sql.Message.Should().Contain("IX_AspNetUsers_AzureTag",
            "the constant in ConcurrencyRetry must be the name SQL Server actually reports");

        ConcurrencyRetry.IsRegistrationDuplicate(thrown.Which).Should().BeTrue(
            "an AzureTag race loser must reach the enumeration-neutral 409, not a 500");
    }

    [SqlServerFact]
    public async Task ADuplicateEmailIsClassifiedAsARegistrationDuplicate()
    {
        var (db, existing) = await SeedUserAsync();

        db.Users.Add(NewUser(azureTag: $"other_{Unique()}", email: existing.Email!));

        var thrown = await ((Func<Task>)(() => db.SaveChangesAsync())).Should()
            .ThrowAsync<DbUpdateException>();

        var sql = thrown.Which.InnerException.Should().BeOfType<SqlException>().Subject;
        sql.Number.Should().BeOneOf(2601, 2627);
        sql.Message.Should().Contain("EmailIndex",
            "Identity's index keeps its own name after AddUniqueEmailIndex made it unique");

        ConcurrencyRetry.IsRegistrationDuplicate(thrown.Which).Should().BeTrue();
    }

    [SqlServerFact]
    public async Task AViolationOnADifferentIndexIsNotARegistrationDuplicate()
    {
        /*
          The narrowing, which is the half that rots. If this returned true for any unique violation,
          the catch it guards would swallow unrelated write failures again — and, since ADR-0037, rob
          the execution strategy of the retry it performs on a transient.

          Provoked on IX_Accounts_AccountNumber: a real violation, on a real index that is not ours.
        */
        var (db, existing) = await SeedUserAsync();

        var account = new Account
        {
            UserId = existing.Id,
            User = existing,
            // Unique per run. A FIXED number here was a real bug: this suite shares the
            // AzureBankTests database, so the row survived the first execution and every later run
            // failed on the SEED rather than on the assertion — which is how it was found, when a
            // mutation run turned this test red for a reason that had nothing to do with the
            // mutation.
            AccountNumber = $"AB-{Random.Shared.Next(1000, 10000)}-{Random.Shared.Next(1000, 10000)}-{Random.Shared.Next(10, 100)}",
            Name = "First",
            Type = Shared.Enums.AccountType.Checking,
            Balance = 0m,
            IsPrimary = true,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        db.Accounts.Add(new Account
        {
            UserId = existing.Id,
            User = existing,
            AccountNumber = account.AccountNumber, // duplicate: IX_Accounts_AccountNumber
            Name = "Clash",
            Type = Shared.Enums.AccountType.Savings,
            Balance = 0m,
            IsPrimary = false,
        });

        var thrown = await ((Func<Task>)(() => db.SaveChangesAsync())).Should()
            .ThrowAsync<DbUpdateException>();

        var sql = thrown.Which.InnerException.Should().BeOfType<SqlException>().Subject;
        sql.Number.Should().BeOneOf(2601, 2627);
        sql.Message.Should().Contain("IX_Accounts_AccountNumber",
            "the point is a real unique violation on a DIFFERENT index");

        ConcurrencyRetry.IsRegistrationDuplicate(thrown.Which).Should().BeFalse(
            "only the AzureTag and email races may be neutralised to a 409");
    }

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];

    private static ApplicationUser NewUser(string azureTag, string email) => new()
    {
        Id = Guid.CreateVersion7(),
        UserName = Guid.CreateVersion7().ToString(),
        NormalizedUserName = Guid.CreateVersion7().ToString().ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        AzureTag = azureTag,
        FirstName = "Dup",
        LastName = "Probe",
    };

    private async Task<(AzureBankDbContext Db, ApplicationUser User)> SeedUserAsync()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        _ = _factory.CreateClient(); // forces the host to build, which runs the migration

        _scope = _factory.Services.CreateScope();
        var db = _scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var unique = Unique();
        var user = NewUser($"dup_{unique}", $"dup{unique}@example.com");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (db, user);
    }

    public void Dispose()
    {
        _scope?.Dispose();
        _factory?.Dispose();
    }
}
