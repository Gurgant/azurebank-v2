using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Proves the unique NULL-filtered NormalizedEmail index (migration AddUniqueEmailIndex,
/// ADR-0013) closes the registration email race on real SQL Server: a burst of parallel
/// registrations with the SAME email (distinct handles) must create EXACTLY ONE account —
/// every other request loses the unique-index write and is neutralised to a 409, never a
/// second 201 or a leaked 500. Identity's RequireUniqueEmail is only an advisory in-process
/// check that a parallel burst can all pass, and InMemory cannot enforce a unique index, so
/// this proof is SQL-gated.
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class RegistrationEmailRaceSqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public RegistrationEmailRaceSqlServerTests(ITestOutputHelper output) => _output = output;

    [SqlServerFact]
    public async Task ParallelSameEmailRegistrations_CreateExactlyOneAccount()
    {
        var client = CreateSqlClient();
        var email = $"emailrace{Guid.NewGuid():N}@example.com";

        // Same email, DISTINCT handles, fired in parallel. Every request passes the advisory
        // FindByEmailAsync pre-check (all see no existing user), so only the unique index can
        // arbitrate the collision — exactly one INSERT may win.
        const int burst = 8;
        var statuses = await Task.WhenAll(
            Enumerable.Range(0, burst).Select(i => RegisterStatusAsync(client, email, i)));
        _output.WriteLine("parallel same-email statuses: " + string.Join(",", statuses.Select(s => (int)s)));

        statuses.Count(s => s == HttpStatusCode.Created).Should().Be(1,
            "exactly one registration with a given email may succeed");
        statuses.Where(s => s != HttpStatusCode.Created)
            .Should().OnlyContain(s => s == HttpStatusCode.Conflict,
                "every loser of the email race must be neutralised to 409 — never a second 201 or a leaked 500");

        (await CountUsersWithEmailAsync(email)).Should().Be(1,
            "the unique index must leave exactly one row for the contested email");
    }

    private async Task<int> CountUsersWithEmailAsync(string email)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.Users.CountAsync(u => u.Email == email);
    }

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    private static async Task<HttpStatusCode> RegisterStatusAsync(HttpClient client, string email, int i)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"emailrace_{i}_{Guid.NewGuid().ToString("N")[..8]}",
            Email = email,
            Password = "SecurePass123!",
            FirstName = "Email",
            LastName = "Race"
        }, Json);
        return response.StatusCode;
    }

    public void Dispose() => _factory?.Dispose();
}
