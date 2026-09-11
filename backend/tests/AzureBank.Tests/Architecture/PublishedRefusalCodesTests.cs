using System.Reflection;
using System.Text.Json;
using AzureBank.Shared.Constants;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Keeps the committed OpenAPI document honest about the 422s that name a code: each names the
/// codes the server answers there, and declares the members those codes carry —
/// <c>INSUFFICIENT_FUNDS</c>'s <c>available</c> and <c>requested</c> included, which the document
/// left out until 2026-09-11.
/// </summary>
/// <remarks>
/// <para>
/// Every code and member below was observed on the real API on 2026-09-11 (Development, a scratch
/// database, bearer calls straight to :7215), and the observation sits beside the assertion that
/// relies on it. The three money moves answered the same balance refusal, this one from
/// <c>POST /api/transfers/internal</c>, the only one of the three never observed before:
/// </para>
/// <code>
/// 422 {"type":"https://httpstatuses.com/422","title":"Unprocessable Entity","status":422,
///      "detail":"Insufficient funds.","instance":"/api/transfers/internal",
///      "errorCode":"INSUFFICIENT_FUNDS","traceId":"…","available":100.2500,"requested":500.5}
/// </code>
/// <para>
/// Reads the COMMITTED file, like <see cref="PublishedDailyLimitTests"/>, because what it asserts
/// is what the document must SAY — which <c>CommittedOpenApiDocumentTests</c> cannot: a comparison
/// with the generator agrees with a wrong declaration as readily as with a right one.
/// </para>
/// </remarks>
public class PublishedRefusalCodesTests
{
    private static readonly string[] MoneyMoves =
        ["/api/transfers", "/api/transfers/internal", "/api/transactions/withdraw"];

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

    /// <summary>
    /// The named member of an operation's 422 schema, or <c>null</c> when it declares none.
    /// </summary>
    private static JsonElement? Member422(string path, string member)
        => Document().GetProperty("paths").GetProperty(path).GetProperty("post")
            .GetProperty("responses").GetProperty("422")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema")
            .TryGetProperty("properties", out var properties)
            && properties.TryGetProperty(member, out var schema)
                ? schema
                : null;

    [Theory]
    [InlineData("/api/transfers")]
    [InlineData("/api/transfers/internal")]
    [InlineData("/api/transactions/withdraw")]
    public void TheThreeMoneyMoves_DeclareWhatTheBalanceRefusalCarries(string path)
    {
        // Observed on each of the three on 2026-09-11, balance 100.25 and 500.5 asked for:
        // "available":100.2500,"requested":500.5 — top-level JSON numbers beside the code.
        foreach (var member in new[] { "available", "requested" })
        {
            var schema = Member422(path, member);
            schema.Should().NotBeNull(
                $"{path} answered {ErrorCodes.InsufficientFunds} with {member} in the body");
            schema!.Value.GetProperty("type").GetString().Should().Be(
                "number", $"{member} is money, and the client formats it in the user's locale");
            schema.Value.GetProperty("description").GetString().Should().Contain(
                ErrorCodes.InsufficientFunds,
                "the member rides only some of this operation's 422 codes, and the description is "
                + "where a client reads which");
        }
    }

    [Fact]
    public void Available_IsDeclaredOnTheThreeMoneyMoves_AndNowhereElse()
    {
        /*
          Neither mint checks the balance: on 2026-09-11 both answered 201 for 500.5 against a
          balance of 100.25, and the transfer each authorised then answered INSUFFICIENT_FUNDS. A
          deposit cannot be refused for lack of money at all. So `available` on any other operation,
          or on the shared component, would be a member no path can send — a contract wider than the
          code. The scan reads every operation rather than a list, so a new one cannot slip past.
        */
        var declaredOn = new List<string>();
        foreach (var path in Document().GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (operation.Value.TryGetProperty("responses", out var responses)
                    && responses.TryGetProperty("422", out var response)
                    && response.TryGetProperty("content", out var content)
                    && content.TryGetProperty("application/json", out var media)
                    && media.GetProperty("schema").TryGetProperty("properties", out var properties)
                    && properties.TryGetProperty("available", out _))
                {
                    declaredOn.Add(path.Name);
                }
            }
        }

        declaredOn.Should().BeEquivalentTo(
            MoneyMoves, "the balance is checked by the three money moves and by nothing else");

        Document().GetProperty("components").GetProperty("schemas").GetProperty("ProblemDetails")
            .GetProperty("properties").TryGetProperty("available", out _).Should().BeFalse(
                "the shared component is untouched: the member rides the operations' own inline "
                + "schemas");
    }

    [Theory]
    [InlineData("/api/transfers", true, true)]
    [InlineData("/api/transfers/authorizations", true, false)]
    [InlineData("/api/transfers/internal", false, true)]
    [InlineData("/api/transactions/withdraw", false, true)]
    public void Requested_NamesEveryCodeThatCarriesIt_AndNoOther(
        string path, bool dailyLimit, bool insufficientFunds)
    {
        /*
          `requested` is the one member two codes share. On POST /api/transfers the document
          called it "DAILY_LIMIT_EXCEEDED only" until 2026-09-11, while the balance refusal there
          carried it too: {"available": 300.0, "requested": 400} on 2026-09-07, and
          "available":100.2500,"requested":500.5 on 2026-09-11. A client that inferred the daily
          refusal from the member's presence was reading the document correctly and still wrong.
        */
        var description =
            Member422(path, "requested")!.Value.GetProperty("description").GetString()!;

        foreach (var (code, carries) in new[]
                 {
                     (ErrorCodes.DailyLimitExceeded, dailyLimit),
                     (ErrorCodes.InsufficientFunds, insufficientFunds),
                 })
        {
            if (carries)
            {
                description.Should().Contain(code, $"{code} on {path} carries requested");
            }
            else
            {
                description.Should().NotContain(code, $"{path} cannot answer {code}");
            }
        }

        if (dailyLimit && insufficientFunds)
        {
            description.Should().NotContain(
                " only", "the member rides both codes here, which is what the old wording denied");
        }
    }

    [Fact]
    public void TheMoneyMoves_NameTheirCodes_AndNotTheTwoExamplesThatCannotHappen()
    {
        /*
          Measured 2026-09-11, one real request each:
            internal transfer, the same account on both sides   -> 400, a validation error
            external transfer to an unknown tag                 -> 404 ACCOUNT_NOT_FOUND
            external transfer to yourself                       -> 422 SELF_TRANSFER_NOT_ALLOWED
            external transfer, recipient's accounts soft-deleted -> 422 RECIPIENT_NO_ACCOUNT
            withdrawal by a user with no PIN enrolled           -> 422 PIN_REQUIRED
          The first two were named as 422 examples until then ("same account transfer", "recipient
          not found"): refusals the server sends with another status.
        */
        var internalTransfer = Description422("/api/transfers/internal");
        internalTransfer.Should().Contain(ErrorCodes.InsufficientFunds);
        internalTransfer.Should().NotContainEquivalentOf(
            "same account", "the validator refuses that pair as a 400 before the service runs");

        var transfer = Description422("/api/transfers");
        foreach (var code in new[]
                 {
                     ErrorCodes.SelfTransferNotAllowed, ErrorCodes.RecipientNoAccount,
                     ErrorCodes.DailyLimitExceeded, ErrorCodes.InsufficientFunds,
                 })
        {
            transfer.Should().Contain(code, "POST /api/transfers answered this 422 code");
        }

        transfer.Should().NotContainEquivalentOf(
            "not found", $"an unknown recipient is a 404, {ErrorCodes.AccountNotFound}");

        var withdraw = Description422("/api/transactions/withdraw");
        withdraw.Should().Contain(ErrorCodes.PinRequired, "a user with no PIN enrolled got it");
        withdraw.Should().Contain(ErrorCodes.InsufficientFunds);
    }

    [Fact]
    public void SetPinAndTheInternalMint_NameTheirCodes_InsteadOfTheBareReasonPhrase()
    {
        /*
          Both published "Unprocessable Entity" until 2026-09-11, because a
          [ProducesResponseType(422)] on the action outranked the transformer entry — ADR-0049 row
          14's trap, the one the external and deletion mints had already been rid of. Measured:
            POST /api/auth/pin, enrolling without the password   -> 422 PASSWORD_REQUIRED
            POST /api/auth/pin, changing without the current PIN -> 422 PIN_REQUIRED
            internal mint, no PIN enrolled                       -> 422 PIN_REQUIRED
            internal mint, the same account on both sides        -> 400, a validation error
        */
        var setPin = Description422("/api/auth/pin");
        setPin.Should().NotBe("Unprocessable Entity");
        setPin.Should().Contain(ErrorCodes.PasswordRequired, "enrolling without the password");
        setPin.Should().Contain(ErrorCodes.PinRequired, "changing without the current PIN");

        var internalMint = Description422("/api/transfers/internal/authorizations");
        internalMint.Should().NotBe("Unprocessable Entity");
        internalMint.Should().Contain(ErrorCodes.PinRequired);
        internalMint.Should().NotContain(
            ErrorCodes.SameAccountTransfer,
            "the service throws it, but the validator answers the same pair first, as a 400");
    }
}
