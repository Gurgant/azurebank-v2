using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
/// it proves the generated client code matches the document. Nothing in CI proved the document
/// matches the code that is supposed to produce it: a stale document regenerates to matching stale
/// output, and every gate stays green. ADR-0043 recorded that gap in as many words and decided
/// something else. This test closes it. It does NOT prove the third claim — that what the code
/// generates tells the truth about what the server DOES at runtime. ADR-0043's own defects were of
/// that kind and this test would have passed on every one of them, because the document the code
/// generated carried the same error. Runtime truth is Schemathesis's job and the real-stack suites',
/// and saying otherwise here would be the overclaim this test was written to remove from <c>ci.yml</c>.
/// </para>
/// <para>
/// THE DOCUMENT COMES FROM THE REAL COMPOSITION, not from a curl. <see cref="IOpenApiDocumentProvider"/>
/// is public in Microsoft.AspNetCore.OpenApi 10.0.1 and is registered by <c>AddOpenApi</c> in every
/// environment; only the <c>MapOpenApi</c> ROUTE is Development-gated. So the Testing host generates
/// the document without HTTP, without starting the API, and without the real secrets a standalone
/// API refuses to boot without. Measured 2026-09-10 before this was written, not assumed: the
/// document this host generates, the one the API serves over HTTP in Development, and the committed
/// file were all 140,432 bytes and byte-identical once the committed file's line endings were
/// normalised.
/// </para>
/// <para>
/// ⚠️ A DIVERGENCE BETWEEN THE TWO HOSTS GOES RED — AND MUST NOT BE REGENERATED AWAY. If the Testing
/// composition ever gains an endpoint or a transformer that Development lacks, or loses one it has,
/// the generated document stops matching a file that was produced from Development, and this fails.
/// Regenerating from here would then copy the TEST host's view into the contract and make the
/// failure go quiet while the contract described a host nobody deploys. A red that names a path the
/// change under review did not touch is that case: fix the divergence, do not regenerate.
/// </para>
/// <para>
/// BYTE-EXACT EXCEPT FOR TWO THINGS THAT BELONG TO THE MACHINE, NOT THE CONTRACT, and the committed
/// file is read as bytes so that nothing else is forgiven — a BOM, which a text read would strip,
/// counts as a difference. The two:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The FILE's line endings. Git stores it as LF (<c>git ls-files --eol</c> reads <c>i/lf w/crlf</c>)
/// and a Windows checkout rewrites all 4,307 of them to CRLF.
/// </description></item>
/// <item><description>
/// Newlines INSIDE string values. Twenty-four strings in the document — seven operation summaries
/// and seventeen descriptions, forty-seven line breaks between them (measured 2026-09-10) — carry
/// the generating machine's newline: <c>\r\n</c> in the committed file, which was generated on
/// Windows. The one traced to its source is a multi-line XML <c>&lt;summary&gt;</c> comment. A Linux
/// runner is expected to produce <c>\n</c>: converting a controller to LF on Windows did NOT change the
/// output, so the newline comes from the platform rather than from the source file, and only a Linux
/// run can show which. Normalising both sides makes the gate hold either way. (Those summaries reach
/// the document at all only because the target meant to stop the XML-comment generator removes
/// nothing — see ADR-0053.)
/// </description></item>
/// </list>
/// <para>
/// AND THE WRITER'S CULTURE IS FIXED, because it is not merely cosmetic. Measured 2026-09-10: written
/// through a <see cref="StringWriter"/> that formats in the current culture, under it-IT, de-DE or
/// fr-FR the six <c>multipleOf</c> constraints serialise as <c>0,01</c> — a bare JSON number with a
/// comma, which is INVALID JSON. The transformers set a <see cref="decimal"/>; the library formats it
/// through the <see cref="TextWriter"/>'s format provider. So the writer is built with
/// <see cref="CultureInfo.InvariantCulture"/>, and that is the line which holds — swapping each in
/// turn showed that pinning <see cref="CultureInfo.CurrentCulture"/> alone does not. Without it this
/// test would fail falsely on such a machine, and regeneration there would write a document no JSON
/// parser accepts. ⚠️ It does NOT reach the running API, which this remark used to say it did:
/// measured 2026-09-11 through the real route under it-IT and de-DE, <c>/openapi/v1.json</c> writes
/// <c>0.01</c> and parses as JSON, because <c>MapOpenApi</c> builds its own writer with
/// <see cref="CultureInfo.InvariantCulture"/>. The risk is confined to a writer this test builds, which
/// is why the test builds it invariant (ADR-0053, the withdrawal note under its Consequences).
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
        JsonNode parsed;
        try
        {
            parsed = JsonNode.Parse(generated)!;
        }
        catch (JsonException exception)
        {
            /*
              NOT DRIFT: THE GENERATOR ITSELF IS BROKEN, and it gets its own message because the raw
              exception names a line number in a document nobody is looking at. Measured 2026-09-10:
              serialised through a writer that formats in the current culture, under it-IT the six
              multipleOf constraints come out as 0,01 and the parse fails right here.
            */
            Assert.Fail(
                $"The API generated a document that is not valid JSON ({exception.Message}). That is not "
                + "drift and regenerating would write it into the contract. See the remarks on this "
                + "class: the known cause is a decimal formatted in the current culture.");
            throw;
        }

        var operations = CountOperations(parsed);
        operations.Should().BeGreaterThanOrEqualTo(
            20,
            "the API publishes 27 operations; a generated document with almost none means the provider "
            + "or the composition is wrong, and comparing two empty documents would prove nothing");

        var path = CommittedPath();

        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            /*
              The RAW generated text is written — not the normalised form the comparison uses — so
              that regeneration on the machine that has always regenerated this file reproduces it
              byte for byte, and never mixes an unrelated newline change into a real one.
            */
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

        // Bytes, decoded without BOM detection, so a BOM added to the committed file is a difference.
        var committed = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));

        if (Normalise(committed) == Normalise(generated))
        {
            return;
        }

        Assert.Fail(
            "docs/api/openapiv1.json is not what the API generates. Every frontend gate regenerates FROM "
            + "this file, so a stale copy passes all of them.\n"
            + Describe(committed, generated) + "\n"
            + "If the paths named above are ones the change under review did not touch, the Testing and "
            + "Development compositions may have diverged: fix that, do not regenerate (see the remarks "
            + "on this class).\n"
            + $"Otherwise regenerate with:  {RegenerateVariable}=1 dotnet test --filter \"FullyQualifiedName~"
            + $"{nameof(CommittedOpenApiDocumentTests)}\"  (from backend/), then regenerate the frontend "
            + "artefacts with `npm run generate:api` and `npm run generate:zod` (from frontend/).");
    }

    private async Task<string> GenerateAsync()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var provider = Factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
            var document = await provider.GetOpenApiDocumentAsync();

            /*
              ⚠️ THE FORMAT PROVIDER ON THIS WRITER IS THE LINE THAT MATTERS, not the CurrentCulture
              assignment above. Measured by swapping each: with CurrentCulture set to it-IT and this
              writer left Invariant the gate still passes; with CurrentCulture it-IT AND a plain
              StringWriter() it fails, because the library formats the decimal multipleOf through the
              TextWriter's provider. The CurrentCulture pin stays as the second line of defence, for any
              transformer that formats text while the document is being GENERATED rather than written.
            */
            using var text = new StringWriter(CultureInfo.InvariantCulture);
            document.SerializeAsV31(new OpenApiJsonWriter(text));
            return text.ToString();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// The file's CRLF line endings, and a CRLF escaped inside a JSON string, both become LF.
    /// </summary>
    /// <remarks>
    /// The lookbehind keeps an escaped BACKSLASH followed by <c>r\n</c> — a string that documents an
    /// escape sequence — from being rewritten; in valid JSON an unescaped <c>\r\n</c> is always CR+LF.
    /// </remarks>
    private static string Normalise(string json) =>
        Regex.Replace(json.Replace("\r\n", "\n"), @"(?<!\\)\\r\\n", @"\n");

    private static string Describe(string committed, string generated)
    {
        JsonNode? left, right;
        try
        {
            left = JsonNode.Parse(Normalise(committed));
            right = JsonNode.Parse(Normalise(generated));
        }
        catch (JsonException exception)
        {
            return $"One of the two is not valid JSON, so no path can be named: {exception.Message}";
        }

        var differences = FirstDifferences(left, right);
        return differences.Count == 0
            ? "The two parse to the same JSON and differ only in bytes — a formatting difference, which can "
              + "come from an edit to the file or from a change in how the serialiser writes it."
            : "First differences, committed → generated:\n  " + string.Join("\n  ", differences);
    }

    private static string CommittedPath()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(
            because: "the gate needs the committed document; one that cannot find it must fail loudly");

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
                    var keys = left.Select(p => p.Key)
                        .Union(right.Select(p => p.Key))
                        .Order(StringComparer.Ordinal);
                    foreach (var key in keys)
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
