using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A wrong PIN at each of the three mints, on SQL Server: the refusal row is committed although the
/// request that caused it fails, and the chain it joined still verifies.
/// </summary>
/// <remarks>
/// <c>RecordRefusalAsync</c> writes on its OWN connection and commits at once, so that the refusal
/// cannot be rolled back with the operation it refuses (ADR-0044). The InMemory provider has no
/// connections and no transactions to prove that against, which is why this lives behind
/// <c>[SqlServerTheory]</c>; <c>AuditTrailPersistenceTests</c> covers the row's shape and the lockout
/// on every run. Until 2026-09-11 a wrong PIN at a mint wrote nothing at all.
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class MintPinRefusalSqlServerTests : IDisposable
{
    private const string Password = "SecurePass123!";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private CustomWebApplicationFactory? _factory;

    [SqlServerTheory]
    [InlineData("external")]
    [InlineData("internal")]
    [InlineData("deletion")]
    public async Task AWrongPinAtAMint_CommitsItsRow_AndTheChainStillVerifies(string mint)
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = _factory.CreateClient();

        var (userId, accountId) = await RegisterAsync(client, withPin: true);
        var (path, body, securityEvent, subject) = await ScenarioAsync(client, mint, accountId);

        var response = await client.PostAsJsonAsync(path, body, Json);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "999999 is not the PIN");

        // A fresh scope, so the answer comes from SQL Server and not from any tracker.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var rows = await db.AuditEvents.AsNoTracking()
            .Where(e => e.ActorUserId == userId && e.Event == securityEvent)
            .ToListAsync();

        rows.Should().ContainSingle("the refused PIN wrote its row, and wrote it once");
        rows[0].Outcome.Should().Be(AuditOutcome.Refused);
        rows[0].Detail.Should().Be(ErrorCodes.InvalidPin);
        rows[0].SubjectId.Should().Be(subject);

        var verification = await scope.ServiceProvider.GetRequiredService<IAuditChain>().VerifyAsync(db);
        verification.IsIntact.Should().BeTrue(because: verification.Reason);
    }

    /// <summary>
    /// The mint under test, asked with the wrong PIN: its path, its body, the event the refusal must
    /// write, and the account that row must name.
    /// </summary>
    private async Task<(string Path, object Body, string Event, Guid Subject)> ScenarioAsync(
        HttpClient client, string mint, Guid accountId)
    {
        switch (mint)
        {
            case "external":
                {
                    var payeeTag = await RegisterPayeeAsync();
                    return ("/api/transfers/authorizations",
                        new TransferAuthorizationRequest
                        {
                            FromAccountId = accountId,
                            RecipientAzureTag = payeeTag,
                            Amount = 10m,
                            Pin = "999999",
                        },
                        SecurityEvents.MoneyTransferRefused, accountId);
                }
            case "internal":
                {
                    var spare = await CreateSpareAccountAsync(client);
                    return ("/api/transfers/internal/authorizations",
                        new InternalTransferAuthorizationRequest
                        {
                            FromAccountId = accountId,
                            ToAccountId = spare,
                            Amount = 10m,
                            Pin = "999999",
                        },
                        SecurityEvents.MoneyTransferRefused, accountId);
                }
            case "deletion":
                {
                    var spare = await CreateSpareAccountAsync(client);
                    return ($"/api/accounts/{spare}/deletion-authorizations",
                        new AccountDeletionAuthorizationRequest { Pin = "999999" },
                        SecurityEvents.AccountDeletionRefused, spare);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(mint), mint, "external, internal or deletion");
        }
    }

    private async Task<(Guid UserId, Guid AccountId)> RegisterAsync(HttpClient client, bool withPin)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"mint_{unique}",
            Email = $"mint{unique}@example.com",
            Password = Password,
            FirstName = "Mint",
            LastName = "Refusal",
        }, Json);
        response.EnsureSuccessStatusCode();

        var registered = (await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json))!.Data!;
        if (withPin)
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
            (await client.PostAsJsonAsync(
                    "/api/auth/pin", new SetPinRequest { Pin = "123456", Password = Password }, Json))
                .EnsureSuccessStatusCode();
        }

        return (registered.User.Id, registered.Account.Id);
    }

    /// <summary>A second user to pay, registered on a client of its own so the actor's stays put.</summary>
    private async Task<string> RegisterPayeeAsync()
    {
        using var payeeClient = _factory!.CreateClient();
        var (payeeId, _) = await RegisterAsync(payeeClient, withPin: false);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Id == payeeId)).AzureTag;
    }

    private static async Task<Guid> CreateSpareAccountAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/accounts", new CreateAccountRequest { Name = "Spare", Type = AccountType.Savings }, Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(Json))!.Data!.Id;
    }

    public void Dispose() => _factory?.Dispose();
}
