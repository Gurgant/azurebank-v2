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
/// Proves the unique NULL-filtered NormalizedEmail index (migration AddUniqueEmailIndex,
/// ADR-0013) closes the registration email race on real SQL Server AND that uniqueness is
/// enforced on the CASE-INSENSITIVE NormalizedEmail, not the raw Email: a burst of parallel
/// registrations that are MIXED-CASE spellings of ONE logical address (distinct handles) must
/// create EXACTLY ONE account, and every loser must return the enumeration-neutral 409
/// (errorCode REGISTRATION_FAILED) — never a second 201, a leaked 500, or a distinguishing
/// error code. Identity's RequireUniqueEmail is only an advisory in-process check a parallel
/// burst can all pass, and InMemory cannot enforce a unique index, so this proof is SQL-gated.
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
    public async Task ParallelMixedCaseSameEmail_CreateExactlyOneAccount_LosersAreNeutral409()
    {
        var client = CreateSqlClient();
        var logicalEmail = $"emailrace{Guid.NewGuid():N}@example.com";

        // Eight MIXED-CASE spellings of the SAME logical address (distinct handles), fired in
        // parallel. They all normalise to one NormalizedEmail, so only a unique index on the
        // NORMALISED column can arbitrate — an index on the raw Email would let different-case
        // spellings coexist. Each passes the advisory FindByEmailAsync pre-check.
        const int burst = 8;
        var results = await Task.WhenAll(
            Enumerable.Range(0, burst).Select(i => RegisterAsync(client, CaseVariant(logicalEmail, i), i)));

        _output.WriteLine("statuses: " + string.Join(",", results.Select(r => (int)r.Status)));

        results.Count(r => r.Status == HttpStatusCode.Created).Should().Be(1,
            "exactly one registration for a given (normalized) email may succeed");

        var losers = results.Where(r => r.Status != HttpStatusCode.Created).ToList();
        losers.Should().OnlyContain(r => r.Status == HttpStatusCode.Conflict,
            "every loser of the email race must be a 409 — never a second 201 or a leaked 500");
        losers.Should().OnlyContain(r => r.ErrorCode == ErrorCodes.RegistrationFailed,
            "every loser must return the enumeration-NEUTRAL errorCode, never a distinguishing one");

        (await CountUsersByNormalizedEmailAsync(logicalEmail.ToUpperInvariant())).Should().Be(1,
            "the unique index on NormalizedEmail must leave exactly one row for the contested address");
    }

    // Deterministically toggle the case of the local-part letters so the eight spellings are
    // distinct raw strings that all normalise to the same NormalizedEmail.
    private static string CaseVariant(string email, int seed)
    {
        var at = email.IndexOf('@');
        var chars = email.ToCharArray();
        for (var j = 0; j < at; j++)
        {
            if (!char.IsLetter(chars[j])) continue;
            chars[j] = (j + seed) % 2 == 0
                ? char.ToUpperInvariant(chars[j])
                : char.ToLowerInvariant(chars[j]);
        }
        return new string(chars);
    }

    private async Task<int> CountUsersByNormalizedEmailAsync(string normalizedEmail)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.Users.CountAsync(u => u.NormalizedEmail == normalizedEmail);
    }

    private HttpClient CreateSqlClient()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        return _factory.CreateClient();
    }

    private static async Task<(HttpStatusCode Status, string? ErrorCode)> RegisterAsync(
        HttpClient client, string email, int i)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"emailrace_{i}_{Guid.NewGuid().ToString("N")[..8]}",
            Email = email,
            Password = "SecurePass123!",
            FirstName = "Email",
            LastName = "Race"
        }, Json);

        string? errorCode = null;
        if (response.StatusCode != HttpStatusCode.Created)
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("errorCode", out var code))
            {
                errorCode = code.GetString();
            }
        }
        return (response.StatusCode, errorCode);
    }

    public void Dispose() => _factory?.Dispose();
}
