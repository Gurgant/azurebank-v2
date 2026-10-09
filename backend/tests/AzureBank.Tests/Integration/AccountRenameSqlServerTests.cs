using System.Data.Common;
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
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    [SqlServerFact]
    public async Task AClosureThatRacesTheRename_ReturnsNotFoundAndKeepsTheStoredName()
    {
        const string originalName = "Closure target";
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        using var client = _factory.CreateClient();
        var user = await RegisterAsync(client);

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/accounts")
        {
            Content = JsonContent.Create(
                new CreateAccountRequest { Name = originalName, Type = AccountType.Savings }, options: Json)
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token.AccessToken);
        using var created = await client.SendAsync(create);
        created.EnsureSuccessStatusCode();
        var accountId = (await created.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id;

        var race = new OutOfBandClosureInterceptor(SqlServerFactAttribute.ConnectionString!, accountId);
        _factory.AddInterceptor(race);
        race.Arm();

        using var response = await RenameAsync(client, user.Token.AccessToken, accountId);
        var body = await response.Content.ReadAsStringAsync();
        using var laterResponse = await RenameAsync(client, user.Token.AccessToken, accountId);

        // The query filter hides closed rows, so the measurement explicitly reads the retained row.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == accountId);

        _output.WriteLine("case: closure collision");
        _output.WriteLine($"status: {(int)response.StatusCode}");
        _output.WriteLine($"response body: {body}");
        _output.WriteLine($"later rename status: {(int)laterResponse.StatusCode}");
        _output.WriteLine($"race.Fired: {race.Fired}");
        _output.WriteLine($"race.OutOfBandRowsAffected: {race.OutOfBandRowsAffected}");
        _output.WriteLine($"stored name: {account.Name}");
        _output.WriteLine($"stored balance: {account.Balance.ToString(CultureInfo.InvariantCulture)}");
        _output.WriteLine($"stored IsDeleted: {account.IsDeleted}, DeletedAt: {account.DeletedAt:O}");

        using var assertions = new AssertionScope();
        race.Fired.Should().BeTrue("the closure must run for the test to prove a collision");
        race.OutOfBandRowsAffected.Should().Be(1, "the closure must move the target account's rowversion");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a rename that reloads a closed account answers as a later rename does");
        laterResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        account.Name.Should().Be(originalName, "the retry must not rename the closed row");
        account.IsDeleted.Should().BeTrue();
        account.DeletedAt.Should().NotBeNull();
        account.Balance.Should().Be(0m);
    }

    private sealed record Measurement(
        HttpStatusCode Status, bool RaceFired, int OutOfBandRowsAffected,
        string StoredName, decimal StoredBalance);

    private async Task<Measurement> MeasureRenameAsync(bool armRace)
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        using var client = _factory.CreateClient();

        var user = await RegisterAsync(client);
        var accountId = user.Account.Id;

        var race = new OutOfBandDepositInterceptor(
            SqlServerFactAttribute.ConnectionString!, accountId, DepositAmount);
        _factory.AddInterceptor(race);
        if (armRace)
        {
            race.Arm();
        }

        using var response = await RenameAsync(client, user.Token.AccessToken, accountId);
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

    private static async Task<RegisterResponse> RegisterAsync(HttpClient client)
    {
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
        return registered!.Data!;
    }

    private static async Task<HttpResponseMessage> RenameAsync(HttpClient client, string token, Guid accountId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/accounts/{accountId}")
        {
            Content = JsonContent.Create(new UpdateAccountRequest { Name = NewName }, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private sealed class OutOfBandClosureInterceptor(string connectionString, Guid accountId) : DbCommandInterceptor
    {
        private int _armed;
        private int _fired;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);
        public bool Fired => Volatile.Read(ref _fired) == 1;
        public int OutOfBandRowsAffected { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await CloseBeforeUpdateAsync(command, cancellationToken);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await CloseBeforeUpdateAsync(command, cancellationToken);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private async Task CloseBeforeUpdateAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _armed) != 1
                || !command.CommandText.Contains("UPDATE [Accounts]", StringComparison.Ordinal)
                || Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
            {
                return;
            }

            // A second connection commits the closure before the rename sends its stale rowversion.
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var closure = connection.CreateCommand();
            closure.CommandText =
                "UPDATE [Accounts] SET [IsDeleted] = 1, [DeletedAt] = SYSUTCDATETIME(), "
                + "[UpdatedAt] = SYSUTCDATETIME() WHERE [Id] = @id AND [IsDeleted] = 0";
            closure.Parameters.Add(new SqlParameter("@id", accountId));
            OutOfBandRowsAffected = await closure.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
