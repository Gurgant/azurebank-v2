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
/// untouched).
/// </para>
/// <para>
/// ⚠️ THE FOUR MEMBERS ARE NOW DECLARED, and this remark said the opposite until 2026-09-07. It
/// read "the extension members <c>limit/used/requested/resetsAt</c> … stay undeclared by the
/// <c>available/requested</c> precedent", and review asked what that precedent was actually
/// deciding. It was deciding nothing: <c>available</c> appears zero times in the committed
/// document, so <c>INSUFFICIENT_FUNDS</c>'s members are an OMISSION, not a ruling — while
/// ADR-0043's whole thesis is that the document declares the error body so a generated client can
/// branch on it. They are declared on the two operations that can answer the code, in the INLINE
/// 422 schema those operations already carry, and asserted below. The ProblemDetails COMPONENT is
/// untouched, and <c>available</c> / <c>requested</c> are still missing everywhere — named below so
/// the gap is a known small PR rather than a silence.
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

    /// <summary>The 422 body schema an operation publishes, as the document holds it.</summary>
    private static JsonElement Schema422(string path)
        => Document().GetProperty("paths").GetProperty(path).GetProperty("post")
            .GetProperty("responses").GetProperty("422")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");

    /// <summary>
    /// The named member of an operation's 422 schema, or <c>null</c> when it declares none.
    /// </summary>
    /// <remarks>
    /// A 422 schema in this document is either an INLINE object (every business-rule endpoint, and
    /// the idempotent ones, whose bodies are built by the two transformers) or a <c>$ref</c> to the
    /// shared ProblemDetails component (<c>POST /api/transfers/internal/authorizations</c>,
    /// <c>POST /api/auth/pin</c>). A <c>$ref</c> has no <c>properties</c> of its own and declares
    /// exactly what the component declares — which the component test below
    /// (<c>TheProblemDetailsComponent_IsUntouched_AndInsufficientFundsMembersAreStillMissing</c>)
    /// asserts separately — so "declares nothing here" is the honest answer for it, not an error.
    /// </remarks>
    private static JsonElement? Member422(string path, string member)
    {
        if (!Schema422(path).TryGetProperty("properties", out var properties))
        {
            return null;
        }

        return properties.TryGetProperty(member, out var schema) ? schema : null;
    }

    [Theory]
    [InlineData("/api/transfers")]
    [InlineData("/api/transfers/authorizations")]
    public void TheTwoOperationsThatAnswerTheCode_DeclareTheFourMembers(string path)
    {
        /*
          ADR-0043's thesis, applied: the document declares the error body so a generated client can
          branch on it. `DailyLimitExceededException` spreads {limit, used, requested, resetsAt}
          into the top level of the 422, and until this change none of the four appeared in the
          contract — so the SPA could only reach them through hand-written types.

          THE TYPES ARE THE ASSERTION, not merely the presence: a `limit` published as a string
          would generate a string on the client and the branch compares money to text. `resetsAt`
          carries format date-time because it is an instant, and openapi-typescript renders the
          format into schema.d.ts's JSDoc — which is the whole point of publishing it.
        */
        foreach (var member in new[] { "limit", "used", "requested" })
        {
            var schema = Member422(path, member);
            schema.Should().NotBeNull(
                $"{path}'s 422 can answer {ErrorCodes.DailyLimitExceeded}, which carries {member}");
            schema!.Value.GetProperty("type").GetString().Should().Be(
                "number", $"{member} is money and is formatted by the client in the user's locale");
            schema.Value.GetProperty("description").GetString().Should().Contain(
                ErrorCodes.DailyLimitExceeded,
                "the member rides only one of this operation's several 422 codes, and the "
                + "description is where a client reads that");
        }

        var resets = Member422(path, "resetsAt");
        resets.Should().NotBeNull();
        resets!.Value.GetProperty("type").GetString().Should().Be("string");
        resets.Value.GetProperty("format").GetString().Should().Be(
            "date-time", "it is an instant, and the format is what makes it one on the client");
    }

    [Fact]
    public void TheOperationsThatCannotAnswerTheCode_DeclareNoneOfThem()
    {
        // The mirror of TheOtherMoneyMoves_DoNotClaimTheCode, one level down: naming the code in
        // prose and publishing its members are two ways to widen the contract past the code, and an
        // aggregate that bounds external transfers only (ADR-0050 D2) must do neither elsewhere.
        // The deletion mint is included because it is the other MINT carrying an inline 422, and
        // the members were added to a mint's inline schema. This list is not every inline 422 in
        // the document — DELETE /api/accounts/{id} and GET /api/transactions/summary carry one too,
        // and neither is a money move nor a mint; that the whole rest of the document stayed
        // byte-identical is a claim the committed diff carries, not this test.
        foreach (var path in new[]
                 {
                     "/api/transactions/withdraw", "/api/transactions/deposit",
                     "/api/transfers/internal", "/api/transfers/internal/authorizations",
                     "/api/accounts/{id}/deletion-authorizations",
                 })
        {
            foreach (var member in new[] { "limit", "used", "requested", "resetsAt" })
            {
                Member422(path, member).Should().BeNull(
                    $"{path} does not check the day's ceiling, so it cannot answer {member}");
            }
        }
    }

    [Fact]
    public void TheProblemDetailsComponent_IsUntouched_AndInsufficientFundsMembersAreStillMissing()
    {
        /*
          WHAT THIS CHANGE DID NOT DO, pinned so the next reader does not have to diff for it.

          The shared ProblemDetails component still declares its seven members and no extension: the
          four went onto the two operations' own INLINE 422 schemas, which is where this document
          already builds business-rule bodies (BusinessRulesDocumentTransformer,
          IdempotencyOperationTransformer), so nothing that $refs the component moved.

          `available` and `requested` — the pair INSUFFICIENT_FUNDS spreads the same way — are still
          undeclared ANYWHERE. That is deliberate and it is not a decision that they should stay
          hidden: it spans more operations than these two (every money move can answer the code) and
          it is its own small PR. It was the "precedent" this file used to cite for leaving the
          daily-limit four undeclared, which is why the omission is now asserted rather than
          assumed: a silence cannot be a precedent, and a test that names it cannot be one.
        */
        var component = Document().GetProperty("components").GetProperty("schemas")
            .GetProperty("ProblemDetails").GetProperty("properties");

        foreach (var member in new[] { "limit", "used", "requested", "resetsAt", "available" })
        {
            component.TryGetProperty(member, out _).Should().BeFalse(
                $"{member} is not on the shared component; the four ride the two operations' own "
                + "inline 422 schemas");
        }

        Member422("/api/transactions/withdraw", "available").Should().BeNull(
            "INSUFFICIENT_FUNDS's own {available, requested} stay undeclared — named here as an "
            + "open omission, and its own small PR, not a ruling that it should stay that way");
    }
}
