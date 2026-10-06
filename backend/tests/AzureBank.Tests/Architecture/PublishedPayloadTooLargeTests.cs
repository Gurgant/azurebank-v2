using System.Reflection;
using System.Text.Json;
using AzureBank.Shared.Constants;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Keeps the committed OpenAPI document honest about 413 responses: each mint declares a
/// ProblemDetails 413 naming PAYLOAD_TOO_LARGE, and exactly eight operations declare a 413.
/// </summary>
public class PublishedPayloadTooLargeTests
{
    private static readonly string[] ExpectedOperations =
    [
        "POST /api/transactions/deposit",
        "POST /api/transactions/withdraw",
        "POST /api/transfers",
        "POST /api/transfers/internal",
        "POST /api/transfers/authorizations",
        "POST /api/transfers/internal/authorizations",
        "POST /api/transactions/withdraw/authorizations",
        "POST /api/accounts/{id}/deletion-authorizations",
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

    [Theory]
    [InlineData("/api/transfers/authorizations")]
    [InlineData("/api/transfers/internal/authorizations")]
    [InlineData("/api/transactions/withdraw/authorizations")]
    [InlineData("/api/accounts/{id}/deletion-authorizations")]
    public void Mint_declares_413_as_problem_details_naming_its_code(string path)
    {
        var document = Document();
        var operation = document.GetProperty("paths").GetProperty(path).GetProperty("post");
        var responses = operation.GetProperty("responses");

        responses.TryGetProperty("413", out var tooLarge).Should().BeTrue();
        // The money endpoints' code, IDEMPOTENCY_PAYLOAD_TOO_LARGE, contains this one: the code is
        // asked for with the brackets the description puts round it, and the money code refused.
        tooLarge.GetProperty("description").GetString().Should().Contain($"({ErrorCodes.PayloadTooLarge})")
            .And.NotContain(ErrorCodes.IdempotencyPayloadTooLarge);

        var schema = tooLarge.GetProperty("content").GetProperty("application/json").GetProperty("schema");
        schema.GetProperty("$ref").GetString().Should().Be("#/components/schemas/ProblemDetails");
    }

    [Fact]
    public void Exactly_eight_operations_declare_a_413()
    {
        var document = Document();
        var declaring = new List<string>();

        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (operation.Value.TryGetProperty("responses", out var responses)
                    && responses.TryGetProperty("413", out _))
                {
                    declaring.Add($"{operation.Name.ToUpperInvariant()} {path.Name}");
                }
            }
        }

        declaring.Should().BeEquivalentTo(ExpectedOperations);
    }
}
