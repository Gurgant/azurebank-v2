using System.Globalization;
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
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Proves that a rename saves its name and preserves a deposit that moves the account rowversion.
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class AccountRenameSqlServerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string NewName = "Renamed account";
    private const decimal DepositAmount = 5m;

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public AccountRenameSqlServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SqlServerFact]
    public async Task AnUncontendedRename_SavesTheNewName()
    {
        var measurement = await MeasureRenameAsync(armRace: false);

        using var assertions = new AssertionScope();
        measurement.RaceFired.Should().BeFalse("the control leaves the interceptor unarmed");
        measurement.OutOfBandRowsAffected.Should().Be(0);
        measurement.Status.Should().Be(HttpStatusCode.OK);
        measurement.StoredName.Should().Be(NewName);
        measurement.StoredBalance.Should().Be(0m);
    }

    [SqlServerFact]
    public async Task ADepositThatRacesTheRename_PreservesTheDepositAndSavesTheNewName()
    {
        var measurement = await MeasureRenameAsync(armRace: true);

        using var assertions = new AssertionScope();
        measurement.RaceFired.Should().BeTrue(
            "the out-of-band deposit must run for the test to prove a collision");
        measurement.OutOfBandRowsAffected.Should().Be(1,
            "the deposit must move the rowversion of the account being renamed");
        measurement.Status.Should().Be(HttpStatusCode.OK,
            "account metadata uses server-side concurrency resolution and last-write-wins");
        measurement.StoredName.Should().Be(NewName);
        measurement.StoredBalance.Should().Be(DepositAmount,
            "the rename must preserve the deposit from the second connection");
    }

    private sealed record Measurement(
        HttpStatusCode Status, bool RaceFired, int OutOfBandRowsAffected,
        string StoredName, decimal StoredBalance);

    private async Task<Measurement> MeasureRenameAsync(bool armRace)
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        using var client = _factory.CreateClient();

        var unique = Guid.NewGuid().ToString("N")[..8];
        using var registration = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"rename_{unique}",
            Email = $"rename{unique}@example.com",
            Password = "TestPass123!",
            FirstName = "Rename",
            LastName = "Prover"
        }, Json);
        registration.EnsureSuccessStatusCode();
        var registered = await registration.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        var user = registered!.Data!;
        var accountId = user.Account.Id;

        var race = new OutOfBandDepositInterceptor(
            SqlServerFactAttribute.ConnectionString!, accountId, DepositAmount);
        _factory.AddInterceptor(race);
        if (armRace)
        {
            race.Arm();
        }

        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/accounts/{accountId}")
        {
            Content = JsonContent.Create(new UpdateAccountRequest { Name = NewName }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token.AccessToken);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // A fresh scope reads the stored values independently of the rename's change tracker.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId);

        // Both cases report every observation before an assertion can fail.
        _output.WriteLine($"case: {(armRace ? "collision" : "control")}");
        _output.WriteLine($"status: {(int)response.StatusCode}");
        _output.WriteLine($"response body: {body}");
        _output.WriteLine($"race.Fired: {race.Fired}");
        _output.WriteLine($"race.OutOfBandRowsAffected: {race.OutOfBandRowsAffected}");
        _output.WriteLine($"stored name: {account.Name}");
        _output.WriteLine($"stored balance: {account.Balance.ToString(CultureInfo.InvariantCulture)}");

        return new Measurement(
            response.StatusCode, race.Fired, race.OutOfBandRowsAffected, account.Name, account.Balance);
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
