using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The committed OpenAPI document must be EXACTLY what the API generates today (ADR-0053).
/// </summary>
/// <remarks>
/// <para>
/// THREE CLAIMS, AND THIS TEST PROVES ONE OF THEM. The frontend drift gate in <c>ci.yml</c>
/// regenerates <c>schema.d.ts</c> and the Zod schemas FROM the committed document and compares, so
/// it proves the generated client code matches the document. Nothing proved the document matches the
/// code that is supposed to produce it: a stale document regenerates to matching stale output, and
/// every gate stays green. ADR-0043 recorded that gap in as many words and decided something else.
/// This test closes it. It does NOT prove the third claim — that what the code generates tells the
/// truth about what the server DOES at runtime. ADR-0043's own defects were of that kind (a
/// transformer declaring 400s the router can never produce) and this test would have passed on every
/// one of them, because the document the code generated carried the same error. Runtime truth is
/// Schemathesis's job and the real-stack suites', and saying otherwise here would be the overclaim
/// this test was written to remove from <c>ci.yml</c>.
/// </para>
/// <para>
/// THE DOCUMENT COMES FROM THE REAL COMPOSITION, not from a curl. <see cref="IOpenApiDocumentProvider"/>
/// is public in Microsoft.AspNetCore.OpenApi 10.0.1 and is registered by <c>AddOpenApi</c> in every
/// environment; only the <c>MapOpenApi</c> ROUTE is Development-gated. So the Testing host generates
/// the document without HTTP, without starting the API, and without the real secrets a standalone
/// API refuses to boot without. Measured 2026-09-10 before this was written, not assumed: the
/// document this host generates, the one the API serves over HTTP in Development, and the committed
/// file were all 140,432 bytes and byte-identical once the committed file's line endings were
/// normalised. If the Testing composition ever diverges from Development's, this test goes red — loud,
/// which is the safe direction.
/// </para>
/// <para>
/// BYTE-EXACT, NOT SEMANTIC, because regeneration is deterministic and measured to reproduce the file
/// byte for byte (the <c>servers</c> block, which would otherwise carry the requesting host, is
/// stripped by <c>NoServersDocumentTransformer</c> precisely so that regeneration is idempotent). A
/// semantic comparison would pass a hand-edit that only reordered keys, which is still a document
/// nobody generated. Only the line endings are normalised: git stores this file as LF and a Windows
/// checkout rewrites it to CRLF, and that difference is the working tree's, not the contract's.
/// </para>
/// </remarks>
public class CommittedOpenApiDocumentTests : IntegrationTestBase
{
    /// <summary>
    /// Set to <c>1</c> to overwrite the committed document with the generated one instead of comparing.
    /// </summary>
    private const string RegenerateVariable = "AZUREBANK_REGENERATE_OPENAPI";

    public CommittedOpenApiDocumentTests(CustomWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task TheCommittedDocument_IsExactlyWhatTheApiGenerates()
    {
        var generated = await GenerateAsync();

        /*
          THE GUARD ON THE GUARD, in this suite's idiom. If the provider resolved a document for the
          wrong name, or the host composed without the controllers, the generated document would be
          nearly empty — and a nearly empty document compared against a nearly empty regenerated
          file is agreement about nothing. The floor sits under the 27 operations measured on
          2026-09-10 so that deleting an endpoint does not trip it; losing the controllers does.
        */
        var operations = CountOperations(JsonNode.Parse(generated)!);
        operations.Should().BeGreaterThanOrEqualTo(
            20,
            "the API publishes 27 operations; a generated document with almost none means the provider "
            + "or the composition is wrong, and comparing two empty documents would prove nothing");

        var path = CommittedPath();

        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            await File.WriteAllTextAsync(path, generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            /*
              REGENERATION ALWAYS FAILS, on purpose. A mode that wrote the file and then passed would
              turn this gate into a no-op the moment the variable leaked into a CI environment: the
              build would regenerate the document it was meant to check and report agreement with
              itself. Failing after the write means the flag can only ever make a build red.
            */
            Assert.Fail(
                $"Regenerated {path} from the API's current composition ({operations} operations). "
                + $"Review the diff, then re-run WITHOUT {RegenerateVariable} to confirm it now passes. "
                + "This mode always fails so that it can never pass a build.");
        }

        var committed = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n");

        if (committed == generated)
        {
            return;
        }

        var differences = FirstDifferences(JsonNode.Parse(committed), JsonNode.Parse(generated));
        var summary = differences.Count == 0
            ? "The two parse to the same JSON but differ in bytes, so the committed file was reformatted "
              + "by hand rather than regenerated."
            : "First differences, committed → generated:\n  " + string.Join("\n  ", differences);

        Assert.Fail(
            "docs/api/openapiv1.json is not what the API generates. Every frontend gate regenerates FROM "
            + "this file, so a stale copy passes all of them.\n"
            + summary + "\n"
            + $"Regenerate with:  {RegenerateVariable}=1 dotnet test --filter \"FullyQualifiedName~"
            + $"{nameof(CommittedOpenApiDocumentTests)}\"  (from backend/), then regenerate the frontend "
            + "artefacts with `npm run generate:api` and `npm run generate:zod` (from frontend/).");
    }

    private async Task<string> GenerateAsync()
    {
        var provider = Factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync();

        using var text = new StringWriter();
        document.SerializeAsV31(new OpenApiJsonWriter(text));
        return text.ToString();
    }

    private static string CommittedPath()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(because: "the gate needs the committed document; one that cannot find it must fail loudly");

        var path = Path.Combine(dir!.FullName, "docs", "api", "openapiv1.json");
        File.Exists(path).Should().BeTrue(because: $"the published contract is expected at {path}");
        return path;
    }

    private static int CountOperations(JsonNode document)
    {
        string[] methods = ["get", "put", "post", "delete", "patch", "head", "options", "trace"];
        return document["paths"]!.AsObject()
            .Sum(path => path.Value!.AsObject().Count(member => methods.Contains(member.Key)));
    }

    /// <summary>
    /// The first few JSON paths at which two documents disagree, so a failure names WHERE rather than
    /// dumping 140 KB of text twice.
    /// </summary>
    private static List<string> FirstDifferences(JsonNode? committed, JsonNode? generated, int limit = 8)
    {
        var found = new List<string>();
        Walk(committed, generated, "$", found, limit);
        return found;

        static void Walk(JsonNode? a, JsonNode? b, string at, List<string> found, int limit)
        {
            if (found.Count >= limit)
            {
                return;
            }

            switch (a, b)
            {
                case (JsonObject left, JsonObject right):
                    foreach (var key in left.Select(p => p.Key).Union(right.Select(p => p.Key)).Order(StringComparer.Ordinal))
                    {
                        var inLeft = left.TryGetPropertyValue(key, out var l);
                        var inRight = right.TryGetPropertyValue(key, out var r);
                        if (!inLeft)
                        {
                            found.Add($"{at}.{key}: only in the generated document");
                        }
                        else if (!inRight)
                        {
                            found.Add($"{at}.{key}: only in the committed file");
                        }
                        else
                        {
                            Walk(l, r, $"{at}.{key}", found, limit);
                        }

                        if (found.Count >= limit)
                        {
                            return;
                        }
                    }

                    break;

                case (JsonArray left, JsonArray right):
                    if (left.Count != right.Count)
                    {
                        found.Add($"{at}: {left.Count} items committed, {right.Count} generated");
                    }

                    for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
                    {
                        Walk(left[i], right[i], $"{at}[{i}]", found, limit);
                    }

                    break;

                default:
                    if (!JsonNode.DeepEquals(a, b))
                    {
                        found.Add($"{at}: {Clip(a)} → {Clip(b)}");
                    }

                    break;
            }
        }

        static string Clip(JsonNode? node)
        {
            var text = node?.ToJsonString() ?? "null";
            return text.Length <= 70 ? text : text[..67] + "...";
        }
    }
}
