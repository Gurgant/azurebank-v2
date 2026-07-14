using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Services.Implementations;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Proves the ADR-0011 rehash-on-use migration on real SQL Server: a legacy
/// (un-peppered) PIN hash still verifies, and on the first successful verify it is
/// transparently upgraded in place to a peppered hash via the relational
/// ExecuteUpdate path — no forced reset, no downtime. InMemory covers the logic; this
/// exercises the set-based UPDATE against the real provider.
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class PinPepperMigrationSqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private CustomWebApplicationFactory? _factory;

    [SqlServerFact]
    public async Task LegacyPinHash_VerifiesAndUpgradesToPeppered_OnSqlServer()
    {
        const string pin = "123456";
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = _factory.CreateClient();

        var azureTag = $"pinmig_{Guid.NewGuid():N}"[..15];
        var token = await RegisterAsync(client, azureTag);
        await SetPinAsync(client, token, pin);

        // Simulate a pre-pepper row: overwrite the stored hash with a legacy
        // (un-peppered) hash of the same PIN, written straight into SQL.
        var legacyHash = new PasswordHasher().HashPin(pin);
        legacyHash.Should().NotContain("keyid");

        Guid userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var user = await db.Users.SingleAsync(u => u.AzureTag == azureTag);
            userId = user.Id;
            user.PinHash = legacyHash;
            await db.SaveChangesAsync();
        }

        // The correct PIN verifies against the legacy hash (no pepper applied) ...
        (await VerifyPinAsync(client, token, pin)).Should().Be(HttpStatusCode.OK);

        // ... and the stored hash is upgraded in place to a peppered (keyid) hash.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            var upgraded = await db.Users.Where(u => u.Id == userId)
                .Select(u => u.PinHash).SingleAsync();
            upgraded.Should().NotBe(legacyHash);
            upgraded.Should().Contain("keyid=1",
                "a legacy PIN hash is rehashed to the active pepper on first successful use");
        }

        // The upgraded hash keeps verifying.
        (await VerifyPinAsync(client, token, pin)).Should().Be(HttpStatusCode.OK);
    }

    private static async Task<string> RegisterAsync(HttpClient client, string azureTag)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = azureTag,
            Email = $"{azureTag}@example.com",
            Password = "TestPass123!",
            FirstName = "Pin",
            LastName = "Mig"
        }, Json);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        return result!.Data!.Token.AccessToken;
    }

    private static async Task SetPinAsync(HttpClient client, string token, string pin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin")
        {
            Content = JsonContent.Create(new SetPinRequest { Pin = pin }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private static async Task<HttpStatusCode> VerifyPinAsync(HttpClient client, string token, string pin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/pin/verify")
        {
            Content = JsonContent.Create(new VerifyPinRequest { Pin = pin }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await client.SendAsync(request)).StatusCode;
    }

    public void Dispose() => _factory?.Dispose();
}
