using System.Net.Http.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transfer;
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

    [Fact]
    public async Task ADeposit_WritesItsRow_NamingTheLedgerRowRatherThanCopyingIt()
    {
        /*
          B1. Until this shipped, the audit trail recorded a renamed handle and not a single movement
          of money — the one thing a bank is audited FOR.

          The assertions below are as interesting for what they refuse as for what they check:
          Detail must be null. Amount, description and account all live on the ledger row that
          SubjectId reaches, and copying them into a table designed never to be purged is how
          ADR-0044 D5 gets broken — an amount tied to an actor id is financial data about an
          identifiable person.
        */
        var (token, userId, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        await DepositAsync(token, accountId, 250m);

        var row = await SingleRowForActorAsync(userId, SecurityEvents.MoneyDeposited);

        row.Outcome.Should().Be(AuditOutcome.Succeeded);
        row.SubjectType.Should().Be("Transaction");
        row.SubjectId.Should().NotBeNull("the row has to name the movement it is evidence of");
        row.Detail.Should().BeNull("the ledger row holds the money detail; this table holds who did it");
        row.RowHash.Should().HaveLength(64, "an unchained row would read as audited and prove nothing");

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var ledger = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == row.SubjectId);
        ledger.AccountId.Should().Be(accountId, "SubjectId must reach the real movement, not any row");
        ledger.Amount.Should().Be(250m);
    }

    [Fact]
    public async Task AWithdrawal_WritesItsOwnEvent_NotTheDepositOne()
    {
        // The negative half: a vocabulary where both money directions shared a name would make the
        // table unable to answer the first question anyone asks of it.
        var (token, userId, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        await SetPinAsync(token); // withdraw is PIN-gated (ADR-0010)
        await DepositAsync(token, accountId, 400m);
        var response = await PostMonetaryAsync(
            "/api/transactions/withdraw",
            new { accountId, amount = 150m, description = "rent", pin = "123456" });
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());

        var row = await SingleRowForActorAsync(userId, SecurityEvents.MoneyWithdrawn);
        row.Outcome.Should().Be(AuditOutcome.Succeeded);
        row.SubjectType.Should().Be("Transaction");
        row.Detail.Should().BeNull();
    }

    [Fact]
    public async Task AnExternalTransfer_WritesOneRow_SubjectedToTheLegTheActorOwns()
    {
        /*
          THE ASSERTION AT THE BOTTOM IS THE POINT, and it was a comment in ADR-0044 and a live SQL
          query before it was a test — which is not the same thing, because a live query does not
          survive a refactor.

          A transfer writes TWO ledger rows and ONE audit row. The subject has to be the OUTGOING
          leg, because the incoming one lands on the PAYEE's account, whose owner is provably not the
          actor: the payee is resolved by handle with no ownership check, and the self-transfer guard
          plus the unique AzureTag index make the two ids differ by construction. Point the row at
          the incoming leg and the audit trail names the wrong person for every transfer ever made —
          silently, and with a green suite.
        */
        var (_, payeeId, _) = await RegisterTestUserAsync();
        string payeeTag;
        using (var lookup = Factory.Services.CreateScope())
        {
            var db = lookup.ServiceProvider.GetRequiredService<AzureBankDbContext>();
            payeeTag = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == payeeId)).AzureTag;
        }

        var (payerToken, payerId, payerAccountId) = await RegisterTestUserAsync();
        SetAuthHeader(payerToken);
        await SetPinAsync(payerToken);
        await DepositAsync(payerToken, payerAccountId, 500m);

        var authorization = await AuthoriseTransferAsync(payerAccountId, payeeTag, 75m);
        var response = await PostMonetaryAsync(
            "/api/transfers",
            new TransferRequest
            {
                FromAccountId = payerAccountId,
                RecipientAzureTag = payeeTag,
                Amount = 75m,
                Description = "audited",
            },
            stepUpAuthorizationId: authorization);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());

        var row = await SingleRowForActorAsync(payerId, SecurityEvents.MoneyTransferred);
        row.Outcome.Should().Be(AuditOutcome.Succeeded);
        row.SubjectType.Should().Be("Transaction");
        row.Detail.Should().BeNull("the ledger rows hold amount and counterparty; this table does not");

        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var subject = await context.Transactions.AsNoTracking()
            .Include(t => t.Account)
            .SingleAsync(t => t.Id == row.SubjectId);

        subject.Type.Should().Be(TransactionType.TransferOut, "the outgoing leg is the act; the incoming one is its consequence");
        subject.Account.UserId.Should().Be(
            payerId,
            "the subject must be the leg the ACTOR owns — the payee's leg would name someone who "
            + "authorised nothing, verified no PIN and minted no authorisation");

        // And exactly one row for two ledger rows: a transfer is one act.
        (await context.AuditEvents.AsNoTracking()
            .CountAsync(e => e.ActorUserId == payerId && e.Event == SecurityEvents.MoneyTransferred))
            .Should().Be(1, "two ledger rows are the bookkeeping of a single act");
        payeeId.Should().NotBe(payerId, "the control: these really are two different users");
    }

    [Fact]
    public async Task AnInternalTransfer_WritesItsOwnEvent_NotTheExternalOne()
    {
        // Distinguishing the two is a regulatory difference, not a cosmetic one: a payment to a
        // third party and a move between one's own accounts do not carry the same weight, and an
        // evidence pack should not have to re-derive which happened from the ledger.
        var (token, userId, firstAccountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await SetPinAsync(token);
        await DepositAsync(token, firstAccountId, 500m);

        var created = await Client.PostAsJsonAsync(
            "/api/accounts",
            new CreateAccountRequest { Name = "Second", Type = AccountType.Savings },
            JsonOptions);
        created.EnsureSuccessStatusCode();
        var secondAccountId = (await created.Content
            .ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions))!.Data!.Id;

        var authorization = await AuthoriseInternalTransferAsync(firstAccountId, secondAccountId, 60m);
        var response = await PostMonetaryAsync(
            "/api/transfers/internal",
            new InternalTransferRequest
            {
                FromAccountId = firstAccountId,
                ToAccountId = secondAccountId,
                Amount = 60m,
                Description = "audited",
            },
            stepUpAuthorizationId: authorization);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());

        var row = await SingleRowForActorAsync(userId, SecurityEvents.MoneyTransferredInternally);
        row.Detail.Should().BeNull();

        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        var subject = await context.Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == row.SubjectId);
        subject.AccountId.Should().Be(firstAccountId, "the subject is the leg money left");

        (await context.AuditEvents.AsNoTracking()
            .CountAsync(e => e.ActorUserId == userId && e.Event == SecurityEvents.MoneyTransferred))
            .Should().Be(0, "an internal move must not be recorded as a payment to a third party");
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
