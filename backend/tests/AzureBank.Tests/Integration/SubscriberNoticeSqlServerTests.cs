using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What only SQL Server can prove about the owed-notice row (ADR-0045): that it shares the
/// enrolment's transaction in BOTH directions, that two runs cannot both mark it delivered, and
/// that the store refuses a half-delivered row.
/// </summary>
/// <remarks>
/// <para>
/// Cannot live on InMemory: that provider has no transactions, so nothing would roll back there
/// whichever way the code was written; it ignores CHECK constraints; and whether it enforces a
/// nullable-<c>DateTime</c> concurrency token is a question, not a fact. Each proof is a real request
/// through the real host against the database named by <c>AZUREBANK_TEST_SQLSERVER</c>, with the
/// failure injected as a genuine server error — the <see cref="OverlongAuditEventInterceptor"/>
/// idiom, so the transaction actually sees the fault.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class SubscriberNoticeSqlServerTests : IDisposable
{
    private const string Password = "TestPass123!";

    private readonly ITestOutputHelper _output;
    private CustomWebApplicationFactory? _factory;

    public SubscriberNoticeSqlServerTests(ITestOutputHelper output) => _output = output;

    [SqlServerFact]
    public async Task WhenTheAuditRowCannotBeWritten_NeitherThePinNorTheNoticeSurvives()
    {
        /*
          D1 THROUGH THE REAL ENDPOINT, for the row this branch adds. The enrolment, its audit row
          and its notice are three statements in one owned transaction; fail the middle one with a
          real truncation error and all three must be gone — no PIN, no evidence claiming one, and
          no notice owed for an enrolment that never happened.
        */
        var (client, email) = await RegisterAsync();

        var fault = new OverlongAuditEventInterceptor(SecurityEvents.PinEnrolled);
        _factory!.AddInterceptor(fault);

        var response = await client.PostAsJsonAsync(
            "/api/auth/pin", new { pin = "424242", password = Password });

        fault.Fired.Should().BeTrue("the test proves nothing if the audit insert never actually failed");
        response.IsSuccessStatusCode.Should().BeFalse(
            "an enrolment that cannot be audited must not be reported as done");
        _output.WriteLine($"audit insert refused -> {(int)response.StatusCode}");

        await AssertNothingSurvivedAsync(email);
    }

    [SqlServerFact]
    public async Task WhenTheNoticeCannotBeWritten_TheEnrolmentIsRefused_AndNoAuditRowClaimsIt()
    {
        /*
          THE OTHER DIRECTION, and the one a notice added after UpdateAsync in a second save would
          fail: the PIN would be set and the audit row present while only the notice insert failed.
          Overflowing the notice's own Event column proves the three still stand or fall together.
        */
        var (client, email) = await RegisterAsync();

        var fault = new OverlongAuditEventInterceptor(SecurityEvents.PinEnrolled, table: "SubscriberNotices");
        _factory!.AddInterceptor(fault);

        var response = await client.PostAsJsonAsync(
            "/api/auth/pin", new { pin = "424242", password = Password });

        fault.Fired.Should().BeTrue("the test proves nothing if the notice insert never actually failed");
        response.IsSuccessStatusCode.Should().BeFalse(
            "an enrolment whose notice cannot be recorded does not happen");
        _output.WriteLine($"notice insert refused -> {(int)response.StatusCode}");

        await AssertNothingSurvivedAsync(email);
    }

    [SqlServerFact]
    public async Task TwoRunsCannotBothMarkOneNoticeDelivered()
    {
        var (client, email) = await RegisterAsync();
        (await client.PostAsJsonAsync("/api/auth/pin", new { pin = "424242", password = Password }))
            .EnsureSuccessStatusCode();

        using var first = _factory!.Services.CreateScope();
        using var second = _factory.Services.CreateScope();
        var firstContext = first.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var secondContext = second.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var owner = await firstContext.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        var seenByFirst = await firstContext.SubscriberNotices.SingleAsync(n => n.UserId == owner.Id);
        var seenBySecond = await secondContext.SubscriberNotices.SingleAsync(n => n.UserId == owner.Id);

        seenByFirst.DeliveredAt = DateTime.UtcNow;
        seenByFirst.DeliveryReceipt = "first-run.eml";
        await firstContext.SaveChangesAsync();

        seenBySecond.DeliveredAt = DateTime.UtcNow;
        seenBySecond.DeliveryReceipt = "second-run.eml";
        var act = () => secondContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "the mark carries WHERE DeliveredAt IS NULL, so the run that loaded an owed row and finds "
            + "it already marked must lose loudly rather than overwrite the first run's receipt");

        using var check = _factory.Services.CreateScope();
        var stored = await check.ServiceProvider.GetRequiredService<AzureBankDbContext>()
            .SubscriberNotices.AsNoTracking().SingleAsync(n => n.UserId == owner.Id);
        stored.DeliveryReceipt.Should().Be("first-run.eml");
    }

    [SqlServerFact]
    public async Task TheDatabaseRefusesAHalfDeliveredRow()
    {
        /*
          The writer is not the only thing that can write (AuditAnchorConfiguration's reasoning), so
          the shape rule is a CHECK constraint and this test drives the constraint, not the tool.
        */
        var (client, email) = await RegisterAsync();
        (await client.PostAsJsonAsync("/api/auth/pin", new { pin = "424242", password = Password }))
            .EnsureSuccessStatusCode();

        using var scope = _factory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var owner = await context.Users.AsNoTracking().SingleAsync(u => u.Email == email);

        var instantWithoutReceipt = () => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE SubscriberNotices SET DeliveredAt = SYSUTCDATETIME() WHERE UserId = {owner.Id}");
        (await instantWithoutReceipt.Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("CK_SubscriberNotices_Delivery");

        var receiptWithoutInstant = () => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE SubscriberNotices SET DeliveryReceipt = 'orphan.eml' WHERE UserId = {owner.Id}");
        (await receiptWithoutInstant.Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("CK_SubscriberNotices_Delivery");
    }

    private async Task<(HttpClient Client, string Email)> RegisterAsync()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.SetConnectionString(SqlServerFactAttribute.ConnectionString!);
        var client = _factory.CreateClient();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"notice{unique}@example.com";
        var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            azureTag = $"notice_{unique}",
            email,
            password = Password,
            firstName = "Notice",
            lastName = "Atomicity",
        });
        register.EnsureSuccessStatusCode();

        var token = (await register.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("token").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, email);
    }

    private async Task AssertNothingSurvivedAsync(string email)
    {
        using var scope = _factory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var stored = await context.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        stored.PinHash.Should().BeNull("the PIN shared the failed save");

        (await context.AuditEvents.AsNoTracking()
            .CountAsync(e => e.ActorUserId == stored.Id && e.Event == SecurityEvents.PinEnrolled))
            .Should().Be(0, "no evidence claims an enrolment that did not happen");

        (await context.SubscriberNotices.AsNoTracking().CountAsync(n => n.UserId == stored.Id))
            .Should().Be(0, "no notice is owed for an enrolment that did not happen");
    }

    public void Dispose() => _factory?.Dispose();
}
