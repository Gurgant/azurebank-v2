using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// Keeps the committed OpenAPI document honest about the errors the API sends.
/// </summary>
/// <remarks>
/// <para>
/// The defect these exist to catch is a contract WIDER than the code, and nothing in the pipeline
/// could see it. The drift gate regenerates the frontend artefacts from this document and compares
/// them, which proves the generated code matches the document and never that the document matches
/// the server. Schemathesis could have caught it and does not: its job in
/// <c>contract-tests.yml</c> ends with <c>|| true # report, don't gate</c>.
/// </para>
/// <para>
/// So for a year the document said 58 refusals carried no body at all, while every one of them
/// answered <c>application/json</c> with seven keys. A client generated from it would have had no
/// type for the field it must branch on.
/// </para>
/// <para>
/// These read the COMMITTED file rather than a live server on purpose: the committed file is what
/// downstream generation consumes, so it is the artefact whose truth matters here. Whether it still
/// matches a running API is a different question, answered by
/// <c>node scripts/openapi-spec.mjs check</c>.
/// </para>
/// </remarks>
public class PublishedErrorContractTests
{
    /// <summary>
    /// A response that cannot occur is as false as a body that is not declared, so both are refused.
    /// Statuses an endpoint can only reach through the shared refusal path — the JWT events and
    /// <c>GlobalExceptionHandler</c> — all of which write ProblemDetails.
    /// </summary>
    private static readonly string[] RefusalStatuses = ["401", "403", "404"];

    /// <summary>
    /// Below this the scan is not reporting a clean document, it is reporting that it found nothing
    /// to read. Measured at 132 responses on the day this was written; the floor is deliberately far
    /// enough below to survive ordinary growth in either direction and still catch a path filter
    /// that has eaten its own input. Same posture as <see cref="SourceHygieneTests"/> after #119.
    /// </summary>
    private const int MinimumResponsesScanned = 100;

    private static readonly string[] HttpMethods =
        ["get", "post", "put", "patch", "delete", "head", "options", "trace"];

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
    public void ProblemDetails_component_declares_errorCode_and_traceId()
    {
        var properties = Document()
            .GetProperty("components").GetProperty("schemas")
            .GetProperty("ProblemDetails").GetProperty("properties");

        properties.TryGetProperty("errorCode", out _).Should().BeTrue(
            because: "every refusal the API produces carries one, and it is the field clients branch on");
        properties.TryGetProperty("traceId", out _).Should().BeTrue(
            because: "it is what turns a user's screenshot into a log lookup");
    }

    [Fact]
    public void ProblemDetails_leaves_errorCode_optional()
    {
        var schema = Document()
            .GetProperty("components").GetProperty("schemas").GetProperty("ProblemDetails");

        var required = schema.TryGetProperty("required", out var r)
            ? r.EnumerateArray().Select(e => e.GetString()).ToArray()
            : [];

        required.Should().NotContain("errorCode", because:
            "seventeen 400s point at this component and a model-state failure is one of the shapes "
            + "they can answer — MEASURED, POST /api/auth/register with a malformed body returns "
            + "{type,title,status,errors,traceId} and no errorCode. Requiring it here would publish "
            + "a contract wider than the code, which is the defect this file exists to prevent");
    }

    [Fact]
    public void No_refusal_is_published_with_an_empty_body()
    {
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var path in Document().GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!HttpMethods.Contains(operation.Name)) continue;
                if (!operation.Value.TryGetProperty("responses", out var responses)) continue;

                foreach (var response in responses.EnumerateObject())
                {
                    scanned++;
                    if (!RefusalStatuses.Contains(response.Name)) continue;

                    var hasBody = response.Value.TryGetProperty("content", out var content)
                        && content.EnumerateObject().Any();

                    if (!hasBody)
                    {
                        offenders.Add($"{operation.Name.ToUpperInvariant()} {path.Name} {response.Name}");
                    }
                }
            }
        }

        scanned.Should().BeGreaterThan(MinimumResponsesScanned, because:
            "a scan that reads nothing passes every assertion below it; this floor is what makes "
            + "'no offenders' mean something");

        offenders.Should().BeEmpty(because:
            "the API answers these with application/json ProblemDetails, so an empty declaration "
            + "tells a generated client there is nothing to read");
    }
}
