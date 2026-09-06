using System.Net;
using System.Net.Http.Json;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// ADR-0049: closing an account is authorised like a transfer — a PIN mints a one-shot
/// authorisation bound to the account, presented in <c>Step-Up-Authorization</c> on
/// <c>DELETE /api/accounts/{id}</c>, and accepted once.
///
/// <para>
/// Every case goes straight to the API through <c>CustomWebApplicationFactory</c>, which runs
/// <c>Program.cs</c> verbatim — so model binding, the exception handler and the validators are the
/// real ones. Status and error codes below are quoted beside the assertion with their provenance,
/// and only what one of the two filed transcripts shows is called measured: the closure guards,
/// the ownership refusals and the success message were MEASURED on the running stack on 2026-09-06
/// at 09:24Z (main d93ba10, before this change — measure-before-2026-09-06.txt, rows D1-D10/R1);
/// what the change introduces was MEASURED on the same stack at 10:44Z on this working tree
/// (measure-after-2026-09-06.txt, rows M0-M5, D1-D16, E1-E2) and cites its row. Anything neither
/// transcript contains is marked as expected from the code, and says which code.
/// </para>
///
/// <para>
/// The load-bearing cases for the ORDER are
/// <see cref="Delete_OnAFundedAccount_WithNoAuthorisation_IsStill422"/> (wire) and
/// <c>AccountServiceTests.DeleteAccountAsync_TheGuardsAnswerBeforeThePresenceCheck</c> (unit): a
/// headerless DELETE on an account that cannot be closed answers the guard's 422, not 401 — the
/// deliberate departure from ADR-0042's ordering that keeps the SPA's real-stack contract suite
/// green across the interim main. Move the presence check above the guards and those go red
/// (observed 2026-09-06 by mutation: 401 on the wire, AuthenticationException in the unit).
/// <see cref="Delete_OnAFundedAccount_WithAValidAuthorisation_Is422_AndSpendsNothing"/> pins
/// something else — that the guards run before the SPEND — and stays green under that reorder,
/// because ValidateAsync is read-only.
/// </para>
/// </summary>
public class AccountDeletionAuthorizationTests : IntegrationTestBase
{
    public AccountDeletionAuthorizationTests(CustomWebApplicationFactory factory) : base(factory) { }

    private const string CorrectPin = "123456";
    private const string WrongPin = "999999";

    private static async Task<string?> ErrorCodeOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }

    private static async Task<string?> DetailOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
    }

    /// <summary>
    /// A user with a PIN, their primary account (which cannot be closed) and a SPARE one (empty,
    /// not primary — the only kind a closure can succeed on).
    /// </summary>
    private async Task<(string Token, Guid UserId, Guid Primary, Guid Spare)> ScenarioAsync(bool enrolPin = true)
    {
        var (token, userId, primary) = await RegisterTestUserAsync();
        if (enrolPin)
        {
            await SetPinAsync(token, CorrectPin);
        }

        SetAuthHeader(token);
        var spare = await CreateSpareAsync("Spare");
        return (token, userId, primary, spare);
    }

    private async Task<Guid> CreateSpareAsync(string name)
    {
        var created = await Client.PostAsJsonAsync("/api/accounts", new CreateAccountRequest
        {
            Name = name,
            Type = AccountType.Savings
        }, JsonOptions);
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<ApiResponse<AccountResponse>>(JsonOptions))!.Data!.Id;
    }

    private Task<HttpResponseMessage> MintAsync(Guid accountId, string pin) =>
        Client.PostAsJsonAsync(
            $"/api/accounts/{accountId}/deletion-authorizations",
            new AccountDeletionAuthorizationRequest { Pin = pin },
            JsonOptions);

    private async Task<StepUpAuthorizationResponse> MintOkAsync(Guid accountId)
    {
        var response = await MintAsync(accountId, CorrectPin);
        response.StatusCode.Should().Be(HttpStatusCode.Created, "minting with a correct PIN must succeed");
        return (await response.Content
            .ReadFromJsonAsync<ApiResponse<StepUpAuthorizationResponse>>(JsonOptions))!.Data!;
    }

    /// <summary>
    /// A DELETE carrying, optionally, an authorisation header. Built by hand because the header is
    /// the whole subject: <c>HttpClient.DeleteAsync</c> has nowhere to put one.
    /// </summary>
    private Task<HttpResponseMessage> DeleteAsync(Guid accountId, Guid? authorizationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/accounts/{accountId}");
        if (authorizationId is { } id)
        {
            request.Headers.Add(StepUpConstants.HeaderName, id.ToString());
        }
        return Client.SendAsync(request);
    }

    private async Task<StepUpAuthorization?> ReadAuthorizationAsync(Guid authorizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.StepUpAuthorizations.AsNoTracking().FirstOrDefaultAsync(a => a.Id == authorizationId);
    }

    private async Task<Account> ReadAccountAsync(Guid accountId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        // IgnoreQueryFilters: a closed account is exactly what half of these tests look for.
        return await db.Accounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == accountId);
    }

    private async Task<int> PinFailedCountAsync(Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).PinAccessFailedCount;
    }

    private async Task<List<AuditEvent>> RowsForActorAsync(Guid userId, string securityEvent)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.ActorUserId == userId && e.Event == securityEvent)
            .ToListAsync();
    }

    /// <summary>
    /// Moves an authorisation's expiry into the past, the way StepUpAuthorizationTests does.
    /// </summary>
    private async Task ExpireAsync(Guid authorizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var authorization = await db.StepUpAuthorizations.SingleAsync(a => a.Id == authorizationId);
        authorization.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Spends an authorisation out of band, through the real service in its own scope, so a later
    /// DELETE presents one that is syntactically fine and already Consumed.
    /// </summary>
    private async Task SpendOutOfBandAsync(Guid userId, Guid authorizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var stepUp = scope.ServiceProvider.GetRequiredService<IStepUpAuthorizationService>();
        await stepUp.ConsumeAsync(userId, authorizationId, consumedByTransactionId: null);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Minting
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mint_WithTheCorrectPin_ReturnsAPendingAuthorisation()
    {
        var (_, userId, _, spare) = await ScenarioAsync();

        // Captured BEFORE minting so the assertion pins the WINDOW (StepUpOptions.Window), not
        // merely "some future time" — the same posture as the transfer mint's test.
        var mintedAt = DateTime.UtcNow;
        var minted = await MintOkAsync(spare);

        // Measured 2026-09-06T10:44Z on the running stack (M5, measure-after-2026-09-06.txt): 201
        // "Account closure authorised", {authorizationId, expiresAt = mint + 2m}, row
        // "AccountDeletion Pending NULL".
        minted.AuthorizationId.Should().NotBeEmpty();
        minted.ExpiresAt.Should().BeCloseTo(mintedAt.AddMinutes(2), TimeSpan.FromSeconds(30));

        var stored = await ReadAuthorizationAsync(minted.AuthorizationId);
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(StepUpAuthorizationStatus.Pending);
        stored.UserId.Should().Be(userId);
        stored.Operation.Should().Be(
            StepUpOperation.AccountDeletion, "a closure authorisation is not a transfer authorisation");
        stored.ConsumedAt.Should().BeNull();
        stored.ConsumedByTransactionId.Should().BeNull();
    }

    [Fact]
    public async Task Mint_WithTheWrongPin_IsRefused_AndCostsAnAttempt()
    {
        var (_, userId, _, spare) = await ScenarioAsync();
        var before = await PinFailedCountAsync(userId);

        var response = await MintAsync(spare, WrongPin);

        // Measured 2026-09-06T10:44Z (M1): 401 INVALID_PIN "Invalid PIN.", PinAccessFailedCount
        // 0 -> 1 — the same answer the transfer mint gives; this endpoint must not be a cheaper PIN
        // oracle than any other.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.InvalidPin);
        (await PinFailedCountAsync(userId)).Should().Be(before + 1, "minting IS the authentication event");

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        (await db.StepUpAuthorizations.CountAsync(a => a.UserId == userId))
            .Should().Be(0, "a refused mint must leave no authorisation behind");
    }

    [Fact]
    public async Task Mint_OnAFundedAccount_IsRefusedBeforeThePin()
    {
        var (token, userId, _, spare) = await ScenarioAsync();
        await DepositAsync(token, spare, 5m);
        var before = await PinFailedCountAsync(userId);

        // A WRONG pin, on purpose: if the guard did not answer first this would be 401 INVALID_PIN
        // and would cost an attempt.
        var response = await MintAsync(spare, WrongPin);

        // Measured 2026-09-06 on the DELETE itself before this change (D1, 09:24Z): 422
        // NON_ZERO_BALANCE, detail "Cannot delete an account with a non-zero balance." The mint
        // shares the guard — measured on the mint itself at 10:44Z (M2, wrong PIN): 422
        // NON_ZERO_BALANCE, PinAccessFailedCount unchanged.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.NonZeroBalance);
        (await DetailOf(response)).Should().Be("Cannot delete an account with a non-zero balance.");
        (await PinFailedCountAsync(userId)).Should().Be(before, "a closure that cannot happen must not spend an attempt");
    }

    [Fact]
    public async Task Mint_OnThePrimary_IsRefusedBeforeThePin()
    {
        var (_, userId, primary, _) = await ScenarioAsync();
        var before = await PinFailedCountAsync(userId);

        var response = await MintAsync(primary, WrongPin);

        // Measured 2026-09-06 on the DELETE itself before this change (D2, 09:24Z) and on the mint
        // at 10:44Z (M3, correct PIN — the after-run did not send a wrong one here): 422
        // PRIMARY_ACCOUNT_DELETE, detail "Cannot delete primary account. Set another account as
        // primary first."
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.PrimaryAccountDelete);
        (await DetailOf(response)).Should().Be("Cannot delete primary account. Set another account as primary first.");
        (await PinFailedCountAsync(userId)).Should().Be(before);
    }

    [Fact]
    public async Task Mint_ForSomeoneElsesAccount_IsRefused_AndCostsNoAttempt()
    {
        var (_, _, _, victimsSpare) = await ScenarioAsync();
        var (attackerToken, attackerId, _, _) = await ScenarioAsync();
        SetAuthHeader(attackerToken);

        var response = await MintAsync(victimsSpare, WrongPin);

        // Measured 2026-09-06 on the DELETE itself before this change (D8/D9, 09:24Z) and on the
        // mint at 10:44Z (D15): 403 ACCESS_DENIED, detail "You do not have access to this
        // account." Ownership is the first rung on the mint too, so the PIN is never consulted.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.AccessDenied);
        (await PinFailedCountAsync(attackerId)).Should().Be(0, "a probe of someone else's account costs nothing");

        // And an unknown account is a 404 for the same reason (measured 2026-09-06: D3 on the
        // DELETE at 09:24Z, M4 on the mint at 10:44Z — ACCOUNT_NOT_FOUND).
        var unknown = await MintAsync(Guid.NewGuid(), CorrectPin);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeOf(unknown)).Should().Be(ErrorCodes.AccountNotFound);
    }

    [Fact]
    public async Task Mint_WithNoPinEnrolled_IsRefused_WithTheGeneralisedSentence()
    {
        var (_, _, _, spare) = await ScenarioAsync(enrolPin: false);

        var response = await MintAsync(spare, CorrectPin);

        // Measured 2026-09-06T10:44Z (M0): 422 PIN_REQUIRED "PIN must be set before authorising
        // this operation." The sentence used to say "a transfer"; since ADR-0049 the rail carries
        // a closure too. The SPA mock (handlers.ts) pins the old sentence and aligns to this
        // measured one in the frontend follow-up.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.PinRequired);
        (await DetailOf(response)).Should().Be("PIN must be set before authorising this operation.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Presence — the refusal that writes a row
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_PresentingNoAuthorisation_IsRefused_AndLeavesARefusalRow()
    {
        /*
          THE POINT OF THE WHOLE CHANGE. Measured 2026-09-06 on main d93ba10, before it (D4 in
          measure-before-2026-09-06.txt, 09:24Z): a DELETE of this shape — empty non-primary
          account — CARRYING a Step-Up-Authorization header answered 200 "Account deleted
          successfully". The transcript records only that D4 carried a header; the probe script
          (del-before-probe.py) sent a random well-formed GUID. The header was never consulted. A
          HEADERLESS DELETE on a spare was NOT measured in that run — the probe's D5 was gated on
          the account surviving D4, and D4 had closed it — and no headerless 200 is claimed from it;
          at d93ba10 AccountController/AccountService read no header at all, so the two took the
          same path (inferred from the code), and ADR-0008's 2026-08-18 correction records a
          headerless DELETE on a spare answering 200 on c146fe9. ADR-0008's table had promised
          level 2 for this row since day one.
        */
        var (_, userId, _, spare) = await ScenarioAsync();

        var response = await DeleteAsync(spare, authorizationId: null);

        // Measured 2026-09-06T10:44Z through the BFF (D1): 401 AUTHORIZATION_REQUIRED "This account
        // closure has not been authorised." — the errorCode survived the BFF, /bff/auth/me straight
        // after answered 200 at level 1, the refusal row was committed and the spare stayed listed.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.AuthorizationRequired);
        (await DetailOf(response)).Should().Be("This account closure has not been authorised.");

        (await ReadAccountAsync(spare)).IsDeleted.Should().BeFalse("a refusal closes nothing");

        // The row survives a request that committed nothing else: RecordRefusalAsync, on its own
        // connection.
        var refusals = await RowsForActorAsync(userId, SecurityEvents.AccountDeletionRefused);
        refusals.Should().ContainSingle();
        refusals[0].Outcome.Should().Be(AuditOutcome.Refused);
        refusals[0].SubjectType.Should().Be("Account");
        refusals[0].SubjectId.Should().Be(spare, "the refusal names the account it was refused against");
        refusals[0].Detail.Should().Be(ErrorCodes.AuthorizationRequired);
        (await RowsForActorAsync(userId, SecurityEvents.AccountDeleted)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnEmptyHeaderIsAnAbsentOne_NotAMalformedOne()
    {
        // [FromHeader] Guid? binds an EMPTY value to null (measured on POST /api/transfers,
        // ADR-0042), and on this bodyless DELETE too — measured 2026-09-06T10:44Z: an empty header
        // (D2) and a whitespace one (D3) both answered 401 AUTHORIZATION_REQUIRED.
        var (_, _, _, spare) = await ScenarioAsync();

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/accounts/{spare}");
        request.Headers.TryAddWithoutValidation(StepUpConstants.HeaderName, string.Empty);
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.AuthorizationRequired);
    }

    [Fact]
    public async Task AMalformedHeaderIsRejectedByModelBinding()
    {
        var (_, userId, _, spare) = await ScenarioAsync();

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/accounts/{spare}");
        request.Headers.Add(StepUpConstants.HeaderName, "not-a-guid");
        var response = await Client.SendAsync(request);

        // Measured 2026-09-06T10:44Z (D4): 400 from MVC model binding before the action runs,
        // model-state {"Step-Up-Authorization": ["The value 'not-a-guid' is not valid."]} and no
        // errorCode — the 400 the DELETE contract always declared and could not reach until it had
        // a header.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(StepUpConstants.HeaderName, "the model-state error is keyed on the header");
        (await ErrorCodeOf(response)).Should().BeNull("a model-state 400 carries no errorCode");

        (await ReadAccountAsync(spare)).IsDeleted.Should().BeFalse();
        (await RowsForActorAsync(userId, SecurityEvents.AccountDeletionRefused)).Should().BeEmpty(
            "model binding refused it before the service could record anything");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Spending
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_WithAMatchingAuthorisation_Succeeds_AndSpendsIt()
    {
        var (_, userId, _, spare) = await ScenarioAsync();
        var minted = await MintOkAsync(spare);

        var response = await DeleteAsync(spare, minted.AuthorizationId);

        // Measured 2026-09-06 before this change (D4, 09:24Z, main d93ba10): a DELETE on an empty
        // spare carrying a random-GUID Step-Up-Authorization header answered 200 "Account deleted
        // successfully" — the header was never read; the headerless case was NOT measured before
        // the change (the probe's D5 was gated on the account surviving D4). Measured after it
        // (D9, 10:44Z): the same 200 with the minted authorisation. The message is unchanged; what
        // changed is what it takes to get it.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ApiResponse>(JsonOptions))!.Message
            .Should().Be("Account deleted successfully");

        // Gone from the owner's view. NOT in either transcript: the before-run did not GET the
        // closed account (D7 not run) and the after-run has no GET-by-id probe either. Expected
        // from the code — AzureBankDbContext's global query filter (!IsDeleted) hides the row from
        // the ownership rung — and the 404 ACCOUNT_NOT_FOUND shape for a hidden id is what D10/D11
        // (a second DELETE, 10:44Z) answered. ADR-0008's 2026-08-18 correction records the owner's
        // GET by id after a delete as 404 ACCOUNT_NOT_FOUND on c146fe9.
        var get = await Client.GetAsync($"/api/accounts/{spare}");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeOf(get)).Should().Be(ErrorCodes.AccountNotFound);
        var account = await ReadAccountAsync(spare);
        account.IsDeleted.Should().BeTrue();
        account.DeletedAt.Should().NotBeNull();

        var spent = await ReadAuthorizationAsync(minted.AuthorizationId);
        spent!.Status.Should().Be(StepUpAuthorizationStatus.Consumed);
        spent.ConsumedAt.Should().NotBeNull();
        spent.ConsumedByTransactionId.Should().BeNull(
            "a closure has no ledger row; the column must not name one that does not exist");

        var closures = await RowsForActorAsync(userId, SecurityEvents.AccountDeleted);
        closures.Should().ContainSingle().Which.SubjectId.Should().Be(spare);
        closures[0].Detail.Should().BeNull("success rows carry no Detail (ADR-0044 D5)");
        (await RowsForActorAsync(userId, SecurityEvents.AccountDeletionRefused)).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_ReusingASpentAuthorisation_IsRefused()
    {
        var (_, userId, _, spare) = await ScenarioAsync();
        var minted = await MintOkAsync(spare);

        // Spent out of band rather than by a first DELETE: after a real closure the account is
        // gone and a second DELETE answers 404 at the ownership rung (see ADeletedAccount_…), which
        // would never reach the authorisation. This is the reference presented AGAIN while the
        // account still stands. RTS Art. 4(1): accepted once.
        await SpendOutOfBandAsync(userId, minted.AuthorizationId);

        var response = await DeleteAsync(spare, minted.AuthorizationId);

        // Expected from the code: 401 AUTHORIZATION_INVALID — uniform with unknown/not-yours.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.AuthorizationInvalid);
        (await ReadAccountAsync(spare)).IsDeleted.Should().BeFalse("a refusal closes nothing");
    }

    [Fact]
    public async Task Delete_WithAnUnknownAuthorisation_IsRefusedTheSameWay()
    {
        var (_, _, _, spare) = await ScenarioAsync();

        var response = await DeleteAsync(spare, Guid.NewGuid());

        // Measured 2026-09-06 before this change (D4, 09:24Z): a random unknown GUID in the header
        // answered 200 — the header was ignored. Measured after it (D5, 10:44Z): 401
        // AUTHORIZATION_INVALID "This authorisation cannot be used."
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.AuthorizationInvalid);
        (await ReadAccountAsync(spare)).IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_WithATransferAuthorisation_ForTheSameAccount_IsRefused()
    {
        /*
          A TRANSFER'S REFERENCE, presented to a closure of the account it was minted from — the
          closest shape an attacker holding the user's transfer flow can obtain — is refused.

          MUTATED 2026-09-06 with the operation name deleted from the binding hash (a test run, not
          a running-stack measurement; output filed in azurebank-work/plans/account-deletion/
          measure-tests-2026-09-06.txt): this test STAYED GREEN and only
          StepUpAuthorizationServiceTests.ComputeBindingHash_ForAccountDeletion… went red. A
          transfer authorisation cannot be minted with the closure's exact shape — the
          transfer mint requires a payee and an amount of at least 0.01, and both are in the hash —
          so on the wire the operation name is defence in depth, not the sole separator. The
          sentence an earlier draft of this comment carried ("drop the operation and this test goes
          red") was wrong, and the unit test is the one that guards the operation name.
        */
        var (token, _, _, spare) = await ScenarioAsync();

        // A payee to mint a transfer authorisation against. The transfer mint checks ownership
        // and resolves the payee; it does not check funds, so an empty spare can mint one.
        var unique = Guid.NewGuid().ToString("N")[..8];
        var payeeTag = $"payee_{unique}";
        (await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = payeeTag,
            Email = $"payee{unique}@example.com",
            Password = TestUserPassword,
            FirstName = "Payee",
            LastName = "User"
        }, JsonOptions)).EnsureSuccessStatusCode();
        SetAuthHeader(token);
        var transferAuthorization = await AuthoriseTransferAsync(spare, payeeTag, 10m, CorrectPin);

        var response = await DeleteAsync(spare, transferAuthorization);

        // Measured 2026-09-06T10:44Z (D6, a transfer authorisation minted from the same spare): 401
        // AUTHORIZATION_INVALID "This authorisation cannot be used." That nothing is spent is
        // expected from the code (ValidateAsync is read-only); the after-run did not read that row.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.AuthorizationInvalid);
        (await ReadAccountAsync(spare)).IsDeleted.Should().BeFalse();
        (await ReadAuthorizationAsync(transferAuthorization))!.Status.Should().Be(
            StepUpAuthorizationStatus.Pending, "the transfer authorisation is still the user's to spend on a transfer");
    }

    [Fact]
    public async Task Delete_WithAnExpiredAuthorisation_IsRefused_AndIsNotSpent()
    {
        var (_, _, _, spare) = await ScenarioAsync();
        var minted = await MintOkAsync(spare);
        await ExpireAsync(minted.AuthorizationId);

        var response = await DeleteAsync(spare, minted.AuthorizationId);

        // Measured 2026-09-06 (E2: a DELETE at 10:47:03Z with an authorisation minted at 10:44:53Z,
        // window 2m): 401 AUTHORIZATION_EXPIRED "This authorisation has expired. Enter your PIN
        // again to confirm." — distinct from INVALID, because the user holds a real authorisation
        // of their own and deserves the sentence that says so; row still Pending,
        // PinAccessFailedCount 0/0. No PIN counter is read HERE: DeleteAccountAsync has no PIN and
        // no IPinVerifier, so an assertion on the counter could not fail for any change to this
        // path (the test used to carry one, and its name promised it).
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.AuthorizationExpired);
        (await ReadAccountAsync(spare)).IsDeleted.Should().BeFalse();
        (await ReadAuthorizationAsync(minted.AuthorizationId))!.Status.Should().Be(
            StepUpAuthorizationStatus.Pending, "an expired authorisation is refused, not spent");
    }

    [Fact]
    public async Task Delete_WithSomeoneElsesAuthorisation_IsRefusedTheSameWay()
    {
        var (_, _, _, ownersSpare) = await ScenarioAsync();
        var ownersAuthorization = await MintOkAsync(ownersSpare);

        var (attackerToken, _, _, attackersSpare) = await ScenarioAsync();
        SetAuthHeader(attackerToken);

        var response = await DeleteAsync(attackersSpare, ownersAuthorization.AuthorizationId);

        // Expected from the code: 401 AUTHORIZATION_INVALID — the SAME answer an unknown reference
        // gets, so the endpoint is not an oracle for which references are live. Not in either
        // transcript in this shape: the after-run's D14 presented the owner's authorisation on the
        // OWNER's account and was refused 403 at the ownership rung; here it is presented on the
        // attacker's own account, so the rung passes and the authorisation itself is read.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.AuthorizationInvalid);
        (await ReadAccountAsync(attackersSpare)).IsDeleted.Should().BeFalse();
        (await ReadAuthorizationAsync(ownersAuthorization.AuthorizationId))!.Status.Should().Be(
            StepUpAuthorizationStatus.Pending, "someone else's attempt must not spend it");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Ordering — guards ahead of the presence check
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_OnAFundedAccount_WithAValidAuthorisation_Is422_AndSpendsNothing()
    {
        var (token, userId, _, spare) = await ScenarioAsync();
        var minted = await MintOkAsync(spare);

        // Money arrives between the mint and the closure.
        await DepositAsync(token, spare, 5m);

        var response = await DeleteAsync(spare, minted.AuthorizationId);

        // Measured 2026-09-06T10:44Z (D12: mint, deposit, DELETE with the valid authorisation): 422
        // NON_ZERO_BALANCE, authorisation still Pending. The guard answers before the
        // authorisation is SPENT, so it is still Pending afterwards; this does not distinguish
        // validate-then-guard from guard-then-validate (validation is read-only) — the ORDER is
        // pinned by Delete_OnAFundedAccount_WithNoAuthorisation_IsStill422 below and by the unit
        // Theory.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.NonZeroBalance);
        (await ReadAuthorizationAsync(minted.AuthorizationId))!.Status.Should().Be(
            StepUpAuthorizationStatus.Pending, "a closure the guards refuse spends nothing");
        (await RowsForActorAsync(userId, SecurityEvents.AccountDeletionRefused)).Should().BeEmpty(
            "the 422 guards are business validation, log-only per ADR-0044");
    }

    [Fact]
    public async Task Delete_OnAFundedAccount_WithNoAuthorisation_IsStill422()
    {
        /*
          GUARDS-FIRST, on the wire. money.contract.test.ts pins a HEADERLESS DELETE on a funded
          account = 422 NON_ZERO_BALANCE against the real stack (measured 2026-09-03), and stays
          green across the interim main only because this order holds. Presence-first would answer
          401 here and turn that suite red from a backend-only change (observed 2026-09-06 by
          mutation: 401 instead of 422). Measured after this change, 2026-09-06T10:44Z (D7): 422
          NON_ZERO_BALANCE. Do not reorder.
        */
        var (token, userId, _, spare) = await ScenarioAsync();
        await DepositAsync(token, spare, 5m);

        var response = await DeleteAsync(spare, authorizationId: null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeOf(response)).Should().Be(ErrorCodes.NonZeroBalance);
        (await RowsForActorAsync(userId, SecurityEvents.AccountDeletionRefused)).Should().BeEmpty(
            "the guard answered; the presence check never ran");
    }

    [Fact]
    public async Task ADeletedAccount_DeletedAgain_Is404_WithNoSecondRefusalRow()
    {
        var (_, userId, _, spare) = await ScenarioAsync();
        var minted = await MintOkAsync(spare);
        (await DeleteAsync(spare, minted.AuthorizationId)).StatusCode.Should().Be(HttpStatusCode.OK);

        // No header at all: if the presence check ran ahead of ownership this would be a 401 and
        // a second refusal row would name an account that no longer stands.
        var again = await DeleteAsync(spare, authorizationId: null);

        // NOT measured before this change: measure-before-2026-09-06.txt has no repeat DELETE of
        // the closed account (its only 404 is D3, a random id). Measured after it,
        // 2026-09-06T10:44Z (D10 with the spent header, D11 with none): 404 ACCOUNT_NOT_FOUND both,
        // refusal rows for the user unchanged — the ownership rung reads through the global query
        // filter (AzureBankDbContext, !IsDeleted) and never reaches the presence check.
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeOf(again)).Should().Be(ErrorCodes.AccountNotFound);
        (await RowsForActorAsync(userId, SecurityEvents.AccountDeletionRefused)).Should().BeEmpty(
            "the ownership rung answered first, and a 404 is not a refused closure");
        (await RowsForActorAsync(userId, SecurityEvents.AccountDeleted)).Should().ContainSingle(
            "one closure, one row");
    }
}
