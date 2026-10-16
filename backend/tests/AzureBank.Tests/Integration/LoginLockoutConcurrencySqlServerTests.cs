using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Proves the LOGIN failure counter is ATOMIC on real SQL Server (ADR-0012): a burst
/// of parallel wrong passwords must still lock the account. Identity's
/// UserManager.AccessFailedAsync does an optimistic-concurrency read-modify-write
/// whose lost update EF silently folds into IdentityResult.ConcurrencyFailure — so a
/// parallel burst could stay under the threshold and never lock. AuthService instead
/// increments AccessFailedCount / sets LockoutEnd in a single set-based ExecuteUpdate,
/// which cannot lose updates. InMemory can't exhibit the race, so this proof is SQL-gated.
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class LoginLockoutConcurrencySqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public LoginLockoutConcurrencySqlServerTests(ITestOutputHelper output) => _output = output;

    private const string Password = "SecurePass123!";

    [SqlServerFact]
    public async Task ExactlyThresholdParallelWrongPasswords_LockTheAccount()
    {
        var client = CreateSqlClient();
        var email = await RegisterAsync(client);

        // Fire EXACTLY MaxLoginAttempts wrong passwords in PARALLEL. With an atomic
        // counter every increment lands, so the threshold is reached and the account
        // locks; a lost-update read-modify-write could leave the count below it.
        var statuses = await Task.WhenAll(
            Enumerable.Range(0, ValidationRules.MaxLoginAttempts)
                .Select(_ => LoginStatusAsync(client, email, "WrongPass999!")));
        _output.WriteLine("parallel wrong-password statuses: " + string.Join(",", statuses.Select(s => (int)s)));

        // The account is now locked: the row holds the lock, and even the CORRECT password is
        // refused, with the 401 a wrong one receives (ADR-0012 decision 3).
        (await ReadLockoutEndAsync(email)).Should().BeAfter(DateTimeOffset.UtcNow,
            "exactly MaxLoginAttempts parallel wrong passwords must lock the account (atomic counter)");
        (await LoginStatusAsync(client, email, Password)).Should().Be(HttpStatusCode.Unauthorized,
            "the right password is refused while the lock stands, as a wrong one is");
    }

    [SqlServerFact]
    public async Task BurstOverThreshold_StaysLocked_NoRaceBypass()
    {
        var client = CreateSqlClient();

        // A fresh user each round exercises the unlocked -> burst -> locked path.
        for (var round = 0; round < 3; round++)
        {
            var email = await RegisterAsync(client);

            await Task.WhenAll(Enumerable.Range(0, ValidationRules.MaxLoginAttempts * 3)
                .Select(_ => LoginStatusAsync(client, email, "WrongPass999!")));

            (await ReadLockoutEndAsync(email)).Should().BeAfter(DateTimeOffset.UtcNow,
                $"round {round}: a burst of parallel wrong passwords must leave the account LOCKED, never bypassed");
            (await LoginStatusAsync(client, email, Password)).Should().Be(HttpStatusCode.Unauthorized,
                $"round {round}: the right password is refused while the lock stands, as a wrong one is");

            // The atomic WHERE-guard leaves NO residual count on a just-locked account,
            // so the next window (after expiry) gets a full budget (ADR-0012).
            (await ReadAccessFailedCountAsync(email)).Should().Be(0,
                $"round {round}: a late concurrent increment must not survive onto a locked row");
        }
    }

    private async Task<DateTimeOffset?> ReadLockoutEndAsync(string email)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.Users.Where(u => u.Email == email)
            .Select(u => u.LockoutEnd).SingleAsync();
    }

    private async Task<int> ReadAccessFailedCountAsync(string email)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.Users.Where(u => u.Email == email)
            .Select(u => u.AccessFailedCount).SingleAsync();
    }

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    private static async Task<string> RegisterAsync(HttpClient client)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var email = $"loginlock{uniqueId}@example.com";
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"loginlock_{uniqueId}",
            Email = email,
            Password = Password,
            FirstName = "Login",
            LastName = "Lock"
        }, Json);
        response.EnsureSuccessStatusCode();
        return email;
    }

    private static async Task<HttpStatusCode> LoginStatusAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = password }, Json);
        return response.StatusCode;
    }

    public void Dispose() => _factory?.Dispose();
}
