using System.Reflection;
using System.Text.Json;
using AzureBank.Shared.Constants;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Keeps the committed OpenAPI document honest about the money bounds the API enforces (ADR-0046).
/// </summary>
/// <remarks>
/// <para>
/// The chain is mechanical — <c>ValidationRules.TransactionMaxAmount</c> → <c>[MoneyRange]</c> → the
/// two schema transformers → <c>docs/api/openapiv1.json</c> → the generated frontend schemas — and it
/// has one manual step: <c>node scripts/openapi-spec.mjs regen</c>, which is deliberately not in
/// CI. So a constant changed without a regen would leave the committed document, and every client
/// generated from it, promising the OLD bound while the server enforced the new one. That is the
/// second of the two drifts ADR-0046 names — the constant changed without a regen — and nothing else
/// in the pipeline can see it:
/// the drift gate proves generated == committed, never committed == server.
/// </para>
/// <para>
/// Reads the COMMITTED file, like <see cref="PublishedErrorContractTests"/>, because the committed
/// file is what downstream generation consumes. Whether it matches a running API is
/// <c>openapi-spec.mjs check</c>'s question.
/// </para>
/// </remarks>
public class PublishedMoneyBoundsTests
{
    /// <summary>
    /// Every request schema that carries a money amount. Listed rather than discovered, so that a
    /// seventh money DTO added without <c>[MoneyRange]</c> is a review conversation, not a silent
    /// pass — and measured: six on the day this was written.
    /// </summary>
    private static readonly string[] MoneyRequestSchemas =
    [
        "DepositRequest",
        "WithdrawRequest",
        "TransferRequest",
        "InternalTransferRequest",
        "TransferAuthorizationRequest",
        "InternalTransferAuthorizationRequest",
    ];

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

    [Fact]
    public void EveryMoneyRequestSchema_PublishesTheBoundsTheServerEnforces()
    {
        var schemas = Document().GetProperty("components").GetProperty("schemas");
        var expectedDescription =
            $"Amount must be between {ValidationRules.DescribeAmount(ValidationRules.TransactionMinAmount)} "
            + $"and {ValidationRules.DescribeAmount(ValidationRules.TransactionMaxAmount)}.";

        foreach (var name in MoneyRequestSchemas)
        {
            schemas.TryGetProperty(name, out var schema).Should().BeTrue(
                because: $"{name} is a money request the document must still describe");
            var amount = schema.GetProperty("properties").GetProperty("amount");

            amount.GetProperty("maximum").GetDecimal().Should().Be(
                ValidationRules.TransactionMaxAmount,
                because: $"{name}.amount.maximum is what every generated client enforces; a constant "
                         + "changed without `openapi-spec.mjs regen` would leave clients promising the old bound");
            amount.GetProperty("minimum").GetDecimal().Should().Be(ValidationRules.TransactionMinAmount);
            amount.GetProperty("multipleOf").GetDecimal().Should().Be(0.01m);
            amount.GetProperty("description").GetString().Should().Be(
                expectedDescription,
                because: "the description is [MoneyRange]'s message plus a period, written by "
                         + "DataAnnotationSchemaTransformer; the wire message carries no period");
        }
    }

    [Fact]
    public void TheBoundIsOneNumber_ForEveryMoneyMove()
    {
        // The affirmative half of ADR-0046: there is no inflow-specific bound on the server and the
        // document, so a client that gives deposits a higher cap is promising what the server refuses.
        var schemas = Document().GetProperty("components").GetProperty("schemas");
        // Compared as decimals, not as rendered strings: a scale difference between two equal values
        // (100000 and 100000.00) is not two bounds.
        var maxima = MoneyRequestSchemas
            .Select(n => schemas.GetProperty(n).GetProperty("properties").GetProperty("amount")
                .GetProperty("maximum").GetDecimal())
            .Distinct()
            .ToList();

        maxima.Should().ContainSingle(because: "one per-transaction cap applies to every money move")
            .Which.Should().Be(ValidationRules.TransactionMaxAmount);
    }
}
