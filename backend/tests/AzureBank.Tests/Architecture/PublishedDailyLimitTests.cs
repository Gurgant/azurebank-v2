using System.Reflection;
using System.Text.Json;
using AzureBank.Shared.Constants;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Keeps the committed OpenAPI document honest about the day's ceiling (ADR-0050): the two
/// endpoints that refuse it NAME the code in their 422 prose, the external mint no longer
/// publishes the bare reason phrase, and the endpoints that do NOT refuse it publish neither that
/// code nor one they cannot answer.
/// </summary>
/// <remarks>
/// <para>
/// Reads the COMMITTED file, like <see cref="PublishedMoneyBoundsTests"/>, because the regen is
/// manual (ADR-0046 D5) and this is the only guard that fails until it is done. Two traps it
/// exists for: a <c>[ProducesResponseType(422)]</c> attribute on the mint action OUTRANKS the
/// transformer entry and publishes "Unprocessable Entity" (ADR-0049 row 14 — the state of the
/// external mint until this change), and on an idempotent endpoint the 422 is written by
/// <c>IdempotencyOperationTransformer</c>, so a code named only in
/// <c>BusinessRulesDocumentTransformer</c> reached nothing.
/// </para>
/// <para>
/// What it deliberately does NOT assert: a schema <c>maximum</c> for the aggregate (there is none —
/// the day's ceiling is not a per-request bound, and <see cref="PublishedMoneyBoundsTests"/> stays
/// untouched), and the extension members <c>limit/used/requested/resetsAt</c> in the ProblemDetails
/// component, which stay undeclared by the <c>available/requested</c> precedent.
/// </para>
/// </remarks>
public class PublishedDailyLimitTests
{
    private static JsonElement Document()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(because: "the guard needs the committed document; one that cannot run must fail loudly");

        var path = Path.Combine(dir!.FullName, "docs", "api", "openapiv1.json");
        File.Exists(path).Should().BeTrue(because: $"the published contract is expected at {path}");

        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static string Description422(string path)
        => Document().GetProperty("paths").GetProperty(path).GetProperty("post")
            .GetProperty("responses").GetProperty("422").GetProperty("description").GetString()!;

    [Fact]
    public void TheExternalMint_NamesTheCode_AndIsNoLongerTheBareReasonPhrase()
    {
        var description = Description422("/api/transfers/authorizations");

        description.Should().NotBe(
            "Unprocessable Entity",
            "the attribute that outranked the transformer entry is gone from AuthoriseTransfer");
        description.Should().Contain(ErrorCodes.DailyLimitExceeded);
        description.Should().Contain(ErrorCodes.PinRequired, "the PIN verifier's own 422 is still reachable there");
        description.Should().Contain("before the PIN", "the placement is part of the contract: no attempt is spent");

        // The mint answers 422 FOUR ways, not two: TransferService.AuthoriseTransferAsync calls
        // ResolveExternalPayeeAsync before _dailyLimit.AssertCanMoveAsync, and that helper throws
        // BusinessRuleException with these two codes. Asserting only the last two is how the
        // undercount this guard exists for stayed green while the prose read as exhaustive.
        description.Should().Contain(
            ErrorCodes.SelfTransferNotAllowed,
            "AuthoriseTransferAsync resolves the payee before the daily check, so the mint answers "
            + "this 422 too and a document naming only the last two codes under-describes it");
        description.Should().Contain(
            ErrorCodes.RecipientNoAccount,
            "same rung: ResolveExternalPayeeAsync, called from AuthoriseTransferAsync before "
            + "_dailyLimit.AssertCanMoveAsync, is the only source of this code on the mint");
    }

    [Fact]
    public void TheTransfer_NamesTheCode_BesideTheIdempotencyClause()
    {
        var description = Description422("/api/transfers");

        description.Should().Contain(ErrorCodes.DailyLimitExceeded);
        description.Should().Contain(
            ErrorCodes.IdempotencyKeyReuse,
            "composing the business rule into the idempotency transformer's 422 must not lose its own clause");
    }

    [Fact]
    public void TheOtherMoneyMoves_DoNotClaimTheCode()
    {
        // The aggregate bounds external transfers only (ADR-0050 D2). A document naming the code on
        // a withdrawal, a deposit or an internal transfer would be a contract wider than the code.
        foreach (var path in new[]
                 {
                     "/api/transactions/withdraw", "/api/transactions/deposit",
                     "/api/transfers/internal", "/api/transfers/internal/authorizations",
                 })
        {
            Description422(path).Should().NotContain(
                ErrorCodes.DailyLimitExceeded, $"{path} does not check the day's ceiling");
        }
    }

    [Fact]
    public void TheDeposit_KeepsOnlyTheIdempotencyClause_AndClaimsNoRefusalItCannotAnswer()
    {
        // IdempotencyOperationTransformer's fallback prose, for an idempotent endpoint with no
        // BusinessRulesDocumentTransformer entry. Its comment says "PublishedDailyLimitTests pins
        // the deposit's prose so the over-claim cannot come back" — this is that test, so the
        // comment names something that exists. Until the ADR-0050 composition the fallback also
        // said "(e.g. INSUFFICIENT_FUNDS)"; TransactionService.DepositAsync throws no
        // BusinessRuleException at all (ownership 404/403, then the ledger write), so a deposit
        // cannot answer that code and the document must not publish it.
        var description = Description422("/api/transactions/deposit");

        description.Should().Contain(
            ErrorCodes.IdempotencyKeyReuse,
            "the one refusal an idempotent endpoint with no business rule of its own can make");
        description.Should().NotContain(
            ErrorCodes.InsufficientFunds,
            "adding money cannot be refused for lack of it, and a published code no path can "
            + "answer is a contract wider than the code");
    }

    [Fact]
    public void TheExtensionMembers_StayUndeclared_ByTheAvailableRequestedPrecedent()
    {
        var properties = Document().GetProperty("components").GetProperty("schemas")
            .GetProperty("ProblemDetails").GetProperty("properties");

        foreach (var member in new[] { "limit", "used", "requested", "resetsAt", "available" })
        {
            properties.TryGetProperty(member, out _).Should().BeFalse(
                $"{member} rides undeclared like available/requested; declaring extension members is "
                + "ADR-0043's own follow-up, not this slice's");
        }
    }
}
