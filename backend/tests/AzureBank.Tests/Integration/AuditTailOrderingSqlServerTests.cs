using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Utilities;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The audit chain's two tail reads state their order where EF can read it, and not only inside
/// their raw SQL. The two proofs run on a host that turns EF's "First without OrderBy and filter"
/// warning into an exception, because the log cannot hold this. Measured on SQL Server before the
/// change: a host's first deposit logged the warning, its second deposit did not, and neither did
/// the first deposit of a second host in the same process. So a test that waits for the line
/// depends on what ran before it, and a test that asserts its absence passes with no fix.
/// The controls run on ordinary hosts and record what reaches SQL Server.
/// </summary>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class AuditTailOrderingSqlServerTests
{
    private const string TailStatement =
        "SELECT TOP 1 [Sequence], [RowHash] FROM [AuditEvents] WITH (UPDLOCK, HOLDLOCK) ORDER BY [Sequence] DESC";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly ITestOutputHelper _output;

    public AuditTailOrderingSqlServerTests(ITestOutputHelper output) => _output = output;

    [SqlServerFact]
    public async Task Deposit_WithUnorderedFirstWarningAsError_Is201()
    {
        using var factory = new ThrowOnUnorderedFirstFactory();
        factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        factory.CaptureLog();
        using var client = factory.CreateClient();
        var (token, _, accountId) = await RegisterAsync(client);
        AssertAnUnorderedFirstIsRefused(factory);

        using var response = await DepositAsync(client, token, accountId);
        foreach (var e in factory.CapturedEvents.Where(e => e.Exception is not null))
        {
            _output.WriteLine(e.Exception!.ToString());
        }
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "the audit tail must be ordered in the EF query as well as in its raw SQL");
    }

    [SqlServerFact]
    public async Task SynchronousSave_WithUnorderedFirstWarningAsError_DoesNotThrow()
    {
        using var factory = new ThrowOnUnorderedFirstFactory();
        factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        using var client = factory.CreateClient();
        var (_, userId, accountId) = await RegisterAsync(client);
        AssertAnUnorderedFirstIsRefused(factory);

        Guid ledgerId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            ledgerId = StageDeposit(db, scope.ServiceProvider.GetRequiredService<IAuditService>(), userId, accountId);
            Action act = () => db.SaveChanges();

            act.Should().NotThrow("the synchronous audit tail must also expose its order to EF");
        }

        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        (await saved.Transactions.CountAsync(t => t.Id == ledgerId)).Should().Be(1);
        (await saved.AuditEvents.CountAsync(e => e.SubjectId == ledgerId)).Should().Be(1);
    }

    // Controls: green before the ordering change and after it. The normal host still sends just
    // one tail command, selecting only the newest row under both locks, in each save funnel.
    [SqlServerFact]
    public async Task Control_Deposit_SendsOneLockedNewestRowTailCommand()
    {
        using var factory = new CustomWebApplicationFactory();
        factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var recorder = new CommandRecordingInterceptor();
        factory.AddInterceptor(recorder);
        using var client = factory.CreateClient();
        var (token, _, accountId) = await RegisterAsync(client);

        recorder.Start();
        using var response = await DepositAsync(client, token, accountId);
        recorder.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        AssertTailCommand(recorder);
    }

    [SqlServerFact]
    public async Task Control_SynchronousSave_SendsOneLockedNewestRowTailCommand()
    {
        using var factory = new CustomWebApplicationFactory();
        factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var recorder = new CommandRecordingInterceptor();
        factory.AddInterceptor(recorder);
        using var client = factory.CreateClient();
        var (_, userId, accountId) = await RegisterAsync(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        StageDeposit(db, scope.ServiceProvider.GetRequiredService<IAuditService>(), userId, accountId);

        recorder.Start();
        db.SaveChanges();
        recorder.Stop();

        AssertTailCommand(recorder);
    }

    private void AssertTailCommand(CommandRecordingInterceptor recorder)
    {
        var command = recorder.Selects.Where(c => c.Contains("[AuditEvents] WITH (UPDLOCK", StringComparison.Ordinal))
            .Should().ContainSingle("an audited save reads the tail exactly once").Subject;
        _output.WriteLine(command);
        command.Should().Contain("[AuditEvents] WITH (UPDLOCK, HOLDLOCK)");
        command.Should().Contain("SELECT TOP 1 [Sequence], [RowHash]");
        command.Should().Contain("ORDER BY [Sequence] DESC");
        command.Should().Contain(TailStatement, "the raw statement reaches the server whole, as it is written");
    }

    /// <summary>
    /// The instrument is in force in this host: a First with no ordering and no filter is refused
    /// there. A host that had lost the setting would pass both proofs by saying nothing.
    /// </summary>
    private static void AssertAnUnorderedFirstIsRefused(CustomWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        Action unordered = () => db.AuditEvents.AsNoTracking().Select(e => e.Sequence).FirstOrDefault();

        unordered.Should().Throw<InvalidOperationException>(
                "the proof is void unless this host turns the warning into an exception")
            .WithMessage("*FirstWithoutOrderByAndFilterWarning*");
    }

    private static Guid StageDeposit(AzureBankDbContext db, IAuditService audit, Guid userId, Guid accountId)
    {
        var account = db.Accounts.Single(a => a.Id == accountId);
        var ledger = new Transaction
        {
            Id = Guid.CreateVersion7(),
            TransactionNumber = IdGenerator.GenerateTransactionNumber(),
            AccountId = account.Id,
            Account = account,
            Type = TransactionType.Deposit,
            Amount = 250m,
            BalanceBefore = account.Balance,
            BalanceAfter = account.Balance + 250m,
            Description = "Audit tail ordering proof",
            Status = TransactionStatus.Completed,
        };
        account.Balance += 250m;
        db.Transactions.Add(ledger);
        audit.Record(SecurityEvents.MoneyDeposited, AuditOutcome.Succeeded,
            actorUserId: userId, subjectType: "Transaction", subjectId: ledger.Id);
        return ledger.Id;
    }

    private static async Task<HttpResponseMessage> DepositAsync(HttpClient client, string token, Guid accountId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(new DepositRequest
            {
                AccountId = accountId,
                Amount = 250m,
                Description = "Audit tail ordering proof",
            }, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }

    private static async Task<(string Token, Guid UserId, Guid AccountId)> RegisterAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        using var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"tail_{unique}",
            Email = $"tail{unique}@example.com",
            Password = "TestPass123!",
            FirstName = "Tail",
            LastName = "Order",
        }, Json);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        result.Should().NotBeNull();
        (result!.Data).Should().NotBeNull();
        return (result.Data!.Token.AccessToken, result.Data.User.Id, result.Data.Account.Id);
    }

    private sealed class ThrowOnUnorderedFirstFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.ConfigureDbContext<AzureBankDbContext>(o =>
                o.ConfigureWarnings(w => w.Throw(CoreEventId.FirstWithoutOrderByAndFilterWarning))));
        }
    }
}
