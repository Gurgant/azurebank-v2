using System.Net.Http.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.User;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Drives each audited action through its real endpoint and then asserts that the ROW EXISTS
/// (ADR-0044).
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS FILE EXISTS, and it is the most important comment in it. The unit tests for these paths
/// hold a <c>Mock&lt;IAuditService&gt;</c> and assert that <c>Record</c> was CALLED. That assertion
/// passes whether or not a row is ever written, because <c>Record</c> deliberately only calls
/// <c>Add</c> — the caller's <c>SaveChanges</c> is what persists it. So "the writer was invoked" and
/// "the evidence exists" are different claims, and only the second one is the feature.
/// </para>
/// <para>
/// MEASURED, not hypothetical: <c>AuthService.SetPinAsync</c> shipped in this branch calling
/// <c>Record</c> AFTER <c>UserManager.UpdateAsync</c> had already saved, with nothing saving
/// afterwards. On the running API, <c>POST /api/auth/pin</c> answered 200, the security log line was
/// emitted, and <c>AuditEvents</c> held ZERO rows — while the unit test was green and the whole
/// 766-test suite was green. <c>UserService</c> had the milder version of the same shape: the rename
/// committed in one save and the row rode a SECOND one, so the two could part company.
/// </para>
/// <para>
/// Every assertion here therefore reads the table. None of them mocks the writer.
/// </para>
/// </remarks>
public class AuditTrailPersistenceTests : IntegrationTestBase
{
    public AuditTrailPersistenceTests(CustomWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task EnrollingAPin_WritesTheRow_AndNotOnlyTheLogLine()
    {
        var (token, userId, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        await SetPinAsync(token);

        var row = await SingleRowForActorAsync(userId, SecurityEvents.PinEnrolled);

        row.Outcome.Should().Be(AuditOutcome.Succeeded);
        row.SubjectType.Should().Be("User");
        row.SubjectId.Should().Be(userId, "an enrolment is an act upon the account it binds the PIN to");
        row.Detail.Should().Be(
            "{\"passwordProved\":true}",
            "B3's evidence pack needs the fact the password was proved, and the log line will not outlive it");
        row.RowHash.Should().HaveLength(64, "an unchained row would read as audited and prove nothing");
    }

    [Fact]
    public async Task RenamingTheHandle_WritesTheRow_CarryingNeitherHandle()
    {
        var (token, userId, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var newHandle = $"renamed_{Guid.NewGuid():N}"[..20];
        var response = await Client.PatchAsJsonAsync(
            "/api/users/me/azuretag", new UpdateAzureTagRequest { AzureTag = newHandle }, JsonOptions);
        response.EnsureSuccessStatusCode();

        var row = await SingleRowForActorAsync(userId, SecurityEvents.AzureTagRenamed);

        row.Outcome.Should().Be(AuditOutcome.Succeeded);
        row.SubjectId.Should().Be(userId);

        // D5, asserted rather than trusted to a comment: a handle is user-chosen and public, which
        // is exactly the descriptive personal data a never-purged table must not accumulate.
        row.Detail.Should().BeNull("neither the old handle nor the new one belongs in this table");
        newHandle.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ClosingAnAccount_WritesTheRow_NamingTheAccountThatIsAboutToBecomeInvisible()
    {
        var (token, userId, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Registration's own account is primary and cannot be closed, so this test opens a second.
        var created = await Client.PostAsJsonAsync(
            "/api/accounts",
            new CreateAccountRequest { Name = "Closable", Type = AccountType.Savings },
            JsonOptions);
        created.EnsureSuccessStatusCode();
        var accountId = (await created.Content
            .ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions))!.Data!.Id;

        (await Client.DeleteAsync($"/api/accounts/{accountId}")).EnsureSuccessStatusCode();

        var row = await SingleRowForActorAsync(userId, SecurityEvents.AccountDeleted);

        row.Outcome.Should().Be(AuditOutcome.Succeeded);
        row.SubjectType.Should().Be("Account");
        row.SubjectId.Should().Be(
            accountId,
            "a soft-deleted account is invisible to every read path afterwards, so this row is the "
            + "only surviving record of which one it was");
    }

    [Fact]
    public async Task ARefusedRefresh_WritesItsRow_EvenThoughNothingElseCommitted()
    {
        /*
          The other half of the contract, and the reason there are two writers. A refusal has no
          business transaction to ride — the one it belongs to is being rolled back — so
          RecordRefusalAsync commits on its own connection. Asserting it here proves the row survives
          a request that changed nothing at all.
        */
        ClearAuthHeader();

        var before = await CountAsync(SecurityEvents.RefreshTokenUnknown);

        var response = await Client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = $"not-a-real-token-{Guid.NewGuid():N}" },
            JsonOptions);

        response.IsSuccessStatusCode.Should().BeFalse("an unknown refresh token must be rejected");

        (await CountAsync(SecurityEvents.RefreshTokenUnknown)).Should().Be(
            before + 1, "the refusal is exactly the case a rollback would otherwise erase");
    }

    [Fact]
    public async Task EverythingWrittenByTheseTests_FormsOneIntactChain()
    {
        // A liveness floor as well as a verdict, per this project's rule: a verification that read
        // zero rows also reports "intact", and that answer is worthless.
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();

        // Drive one more audited action so this test can never pass on an empty table.
        var (token, userId, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await SetPinAsync(token);
        await SingleRowForActorAsync(userId, SecurityEvents.PinEnrolled);

        var verification = await chain.VerifyAsync(context);

        verification.IsIntact.Should().BeTrue(because: verification.Reason);
        verification.Verified.Should().BeGreaterThan(0, "an empty read is not a passing verification");
    }

    private async Task<AuditEvent> SingleRowForActorAsync(Guid actorUserId, string securityEvent)
    {
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        // Filtered by ACTOR, not counted globally: the factory is shared across the class, so other
        // tests' rows are in the table too and a global count would be both flaky and meaningless.
        var rows = await context.AuditEvents
            .AsNoTracking()
            .Where(e => e.ActorUserId == actorUserId && e.Event == securityEvent)
            .ToListAsync();

        rows.Should().HaveCount(
            1, $"exactly one {securityEvent} row must exist for this actor — zero means the row was "
               + "added and never saved, more than one means it was written twice");

        return rows[0];
    }

    private async Task<int> CountAsync(string securityEvent)
    {
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await context.AuditEvents.AsNoTracking().CountAsync(e => e.Event == securityEvent);
    }
}
