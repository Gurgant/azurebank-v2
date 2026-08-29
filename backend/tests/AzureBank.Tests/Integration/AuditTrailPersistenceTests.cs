using System.Net;
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
        /*
          MATCHED, NOT MEASURED, and the difference is the whole assertion. RowHash is nchar(64) —
          fixed length — so an unchained row (AuditService sets RowHash to string.Empty) reads back
          from SQL Server as SIXTY-FOUR SPACES and satisfies HaveLength(64). The length check had
          teeth only on the InMemory provider, which is the half of the suite that cannot see the
          column type at all. A hex pattern fails on spaces and on empty alike.
        */
        row.RowHash.Should().MatchRegex(
            "^[0-9a-f]{64}$",
            "an unchained row reads back as blank padding and would otherwise pass a length check");
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
        ledger.Type.Should().Be(
            TransactionType.Deposit,
            "the subject must be the movement this event NAMES — account and amount alone would also "
            + "fit a withdrawal of the same size on the same account");
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

        /*
          RESOLVE THE SUBJECT, because SubjectType is a hard-coded literal and proves nothing on its
          own. Measured: with this block absent, changing the withdrawal's subjectId from the
          transaction to the ACCOUNT left the whole suite green at 111 + 786 — the row would have
          claimed SubjectType "Transaction" while naming something that is not one, and nothing
          anywhere could tell. SubjectId is a nullable Guid with an index and no foreign key, so
          neither a wrong id nor a missing one is caught by the database either.
        */
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var ledger = await db.Transactions.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == row.SubjectId);

        // SingleOrDefault, not Single: a subject pointing at something that is not a Transaction is
        // the exact defect being guarded, and it should fail with a sentence rather than with
        // "Sequence contains no elements".
        ledger.Should().NotBeNull(
            "SubjectType says \"Transaction\", so SubjectId must resolve to one — it is a nullable "
            + "Guid with no foreign key, so nothing else would catch it naming an account");
        ledger!.Type.Should().Be(TransactionType.Withdrawal);
        ledger.AccountId.Should().Be(accountId, "the subject must reach the movement, not its account");
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

        // The transfers are the only paths that take the funnel's SHORT branch — a caller
        // transaction already exists, so the chain is applied inside it rather than opening one.
        // Nothing else asserts that the row still comes out chained on that branch.
        row.RowHash.Should().MatchRegex("^[0-9a-f]{64}$", "the short branch must still hash the row");
        row.Sequence.Should().BeGreaterThan(0, "and still take its place in the chain");
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
        subject.Type.Should().Be(
            TransactionType.TransferOut,
            "an internal transfer picks the outgoing leg for the same reason an external one does — "
            + "that leg is the act. Asserting the type says so directly, where the account id only "
            + "says it by elimination and would stop meaning anything if the fixture changed");
        subject.AccountId.Should().Be(firstAccountId, "the subject is the leg money left");

        (await context.AuditEvents.AsNoTracking()
            .CountAsync(e => e.ActorUserId == userId && e.Event == SecurityEvents.MoneyTransferred))
            .Should().Be(0, "an internal move must not be recorded as a payment to a third party");
    }

    [Fact]
    public async Task AWithdrawalRefusedForFunds_WritesNoRow_AndThatIsTheDecision()
    {
        /*
          THE ABSENCE IS THE ASSERTION, and it is a correction: the first version of this branch DID
          audit insufficient funds, and ADR-0044 had already decided otherwise before the branch
          existed -- a routine user outcome whose row-per-attempt is an unbounded write into a
          never-purged table. Wiring it would have contradicted the ADR without arguing with it.

          The contention angle is sharper than the ADR stated and is the reason this test exists
          rather than the site simply being deleted. A wrong PIN is BOUNDED: three attempts and
          ADR-0010 locks it. This is not -- a caller can ask for more than they hold forever, at no
          cost, and each attempt would take the chain tail lock that every real money movement queues
          behind. Somebody re-reading the refusal list will eventually think this one was forgotten.
          It was not.
        */
        var (token, userId, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await SetPinAsync(token);
        await DepositAsync(token, accountId, 50m);

        var response = await PostMonetaryAsync(
            "/api/transactions/withdraw",
            new { accountId, amount = 5000m, description = "more than there is", pin = "123456" });
        response.IsSuccessStatusCode.Should().BeFalse("50 does not cover 5000");

        var rows = await RowsForActorAsync(userId, SecurityEvents.MoneyWithdrawalRefused);
        rows.Should().BeEmpty(
            "insufficient funds is a routine outcome the ADR keeps out of the trail on purpose, and "
            + "it is the one refusal on this path nothing bounds");
    }

    [Fact]
    public async Task AWrongPinOnAWithdrawal_WritesItsRow_BecauseThatIsTheAttemptWorthSeeing()
    {
        // A guessed PIN against somebody's balance is the event this table exists for, and until
        // 2026-08-29 it left nothing at all.
        var (token, userId, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await SetPinAsync(token);
        await DepositAsync(token, accountId, 500m);

        var response = await PostMonetaryAsync(
            "/api/transactions/withdraw",
            new { accountId, amount = 10m, description = "guessing", pin = "999999" });
        response.IsSuccessStatusCode.Should().BeFalse("999999 is not the PIN");

        var row = await SingleRowForActorAsync(userId, SecurityEvents.MoneyWithdrawalRefused);
        row.Outcome.Should().Be(AuditOutcome.Refused);
        row.Detail.Should().Be(ErrorCodes.InvalidPin);
        row.SubjectId.Should().Be(accountId, "the account is what the attempt was made against");

        /*
          D5, CHECKED ON THE CLASS RATHER THAN ON THIS ROW. The Detail is asserted exactly above, so
          this adds nothing about THIS row -- it is here to catch a LATER refusal site that decides
          an amount would be useful. The rule is the table, not the call.
        */
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var details = await db.AuditEvents.AsNoTracking()
            .Where(e => e.ActorUserId == userId && e.Detail != null)
            .Select(e => e.Detail!).ToListAsync();
        details.Should().OnlyContain(
            d => !d.Any(char.IsDigit),
            "ADR-0044 D5: no figure may reach a table designed never to be purged");
    }

    [Fact]
    public async Task TheLockoutItself_LeavesARow_AndItIsNotTheWrongPinOne()
    {
        /*
          THE BRANCH THAT RETURNS NOTHING. VerifyPinAsync THROWS PinLockedException once the attempt
          limit is crossed rather than returning false, so the lockout cannot be observed by reading
          the return value -- it needs its own catch, and a catch with no test is a branch nobody has
          entered. Measured: PinService throws its lockout at two places and audits at neither, so
          before this the control that stops a PIN brute-force was completely silent.
        */
        var (token, userId, accountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await SetPinAsync(token);
        await DepositAsync(token, accountId, 500m);

        for (var i = 0; i < ValidationRules.MaxPinAttempts; i++)
        {
            await PostMonetaryAsync(
                "/api/transactions/withdraw",
                new { accountId, amount = 10m, description = "guessing", pin = "999999" });
        }

        // The attempt AFTER the limit: refused by the lockout rather than by the PIN, and it uses
        // the CORRECT PIN on purpose -- that is what makes it the lockout and not another miss.
        var locked = await PostMonetaryAsync(
            "/api/transactions/withdraw",
            new { accountId, amount = 10m, description = "still guessing", pin = "123456" });
        locked.StatusCode.Should().Be(
            HttpStatusCode.TooManyRequests,
            "the correct PIN is refused too once the card is locked -- that is the control working");

        var rows = await RowsForActorAsync(userId, SecurityEvents.MoneyWithdrawalRefused);
        /*
          THE OFF-BY-ONE IS THE SYSTEM, NOT THE TEST -- and the first version of this assertion had
          it wrong. The attempt that CROSSES the threshold never returns false: PinService increments
          and locks in one atomic statement and then throws, so that attempt is recorded as the
          LOCKOUT and not as a wrong PIN. Measured here: three wrong attempts leave TWO InvalidPin
          rows, not three.

          Worth asserting rather than tidying away, because anyone counting InvalidPin rows to answer
          "how many times was the PIN guessed" is short by exactly one, every time.
        */
        rows.Count(r => r.Detail == ErrorCodes.InvalidPin).Should().Be(
            ValidationRules.MaxPinAttempts - 1,
            "the attempt that trips the lock is recorded as the lockout instead");
        rows.Count(r => r.Detail == ErrorCodes.PinLocked).Should().Be(
            2, "the attempt that trips it, and the one refused afterwards by the lock itself");
    }

    [Fact]
    public async Task ATransferWithoutStepUp_IsSubjectedToTheAccountTheMoneyWouldHaveLeft()
    {
        /*
          THE SUBJECT IS THE ASSERTION. An internal transfer resolves BOTH accounts before this
          refusal is reached, so the nearest variable at the call site is the DESTINATION -- and
          naming it would put the refusal on the wrong account while every other assertion here
          still passed. Not hypothetical: writing this change, the first version did exactly that.
          It is the same defect shape AnExternalTransfer_WritesOneRow_SubjectedToTheLegTheActorOwns
          guards on the success path.
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

        // No stepUpAuthorizationId: the header is absent, which is the refusal under test.
        var response = await PostMonetaryAsync(
            "/api/transfers",
            new TransferRequest
            {
                FromAccountId = payerAccountId,
                RecipientAzureTag = payeeTag,
                Amount = 75m,
                Description = "unauthorised",
            });
        response.IsSuccessStatusCode.Should().BeFalse("a transfer without a step-up must be refused");

        var row = await SingleRowForActorAsync(payerId, SecurityEvents.MoneyTransferRefused);
        row.Outcome.Should().Be(AuditOutcome.Refused);
        row.SubjectType.Should().Be("Account");
        row.Detail.Should().Be(ErrorCodes.AuthorizationRequired);
        row.SubjectId.Should().Be(
            payerAccountId,
            "the subject is the account the money would have left, never the destination");
    }

    [Fact]
    public async Task AnInternalTransferWithoutStepUp_NamesTheSOURCEAccount_NotTheDestination()
    {
        /*
          THE ONE THAT WOULD HAVE CAUGHT IT. The external path resolves ONE account before the
          refusal, so pointing the row at "the account" cannot go wrong there. The internal path
          resolves BOTH -- source then destination -- so the nearest variable at the refusal is the
          DESTINATION, and a row subjected to it would name the account the money was going TO while
          every other assertion still passed.

          That is not a hypothetical failure mode. Writing this change, the first version took the
          nearest account and put the refusal on the wrong one; it was caught by reading the
          generated diff, which is luck rather than a guard. This is the guard.
        */
        var (token, userId, firstAccountId) = await RegisterTestUserAsync();
        SetAuthHeader(token);
        await SetPinAsync(token);
        await DepositAsync(token, firstAccountId, 300m);

        var created = await Client.PostAsJsonAsync(
            "/api/accounts",
            new CreateAccountRequest { Name = "Second", Type = AccountType.Savings },
            JsonOptions);
        created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync());
        var secondAccountId = (await created.Content
            .ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions))!.Data!.Id;

        // No stepUpAuthorizationId: the refusal under test.
        var response = await PostMonetaryAsync(
            "/api/transfers/internal",
            new InternalTransferRequest
            {
                FromAccountId = firstAccountId,
                ToAccountId = secondAccountId,
                Amount = 60m,
                Description = "unauthorised",
            });
        response.IsSuccessStatusCode.Should().BeFalse("an internal transfer without a step-up is refused");

        var row = await SingleRowForActorAsync(userId, SecurityEvents.MoneyTransferRefused);
        row.Detail.Should().Be(ErrorCodes.AuthorizationRequired);
        row.SubjectId.Should().Be(
            firstAccountId,
            "the subject is the account the money would have LEFT; naming the destination is the "
            + "exact mistake this test exists for, and both accounts belong to the same actor here "
            + "so nothing else in the row would look wrong");
        row.SubjectId.Should().NotBe(secondAccountId);
    }

    private async Task<List<AuditEvent>> RowsForActorAsync(Guid actorUserId, string securityEvent)
    {
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await context.AuditEvents
            .AsNoTracking()
            .Where(e => e.ActorUserId == actorUserId && e.Event == securityEvent)
            .ToListAsync();
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
