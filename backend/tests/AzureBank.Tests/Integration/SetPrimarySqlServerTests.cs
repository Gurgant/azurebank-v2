using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
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
