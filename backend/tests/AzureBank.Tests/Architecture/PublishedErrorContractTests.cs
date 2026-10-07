using System.Reflection;
using System.Text.Json;
using AzureBank.Api.Attributes;
using AzureBank.Api.Controllers;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
/// <c>contract-tests.yml</c> ends with <c>|| true # report, don't gate</c>. (True until
/// 2026-09-15: Schemathesis is the <c>conformance</c> job in <c>ci.yml</c> now, gating on every
/// PR, and its first run found the two shapes this document had never declared — 415 on every
/// body-taking operation and the route-miss 404 as application/problem+json; ADR-0053 D6.)
/// </para>
/// <para>
/// So the document said 58 refusals carried no body at all. Measured, 53 of them answer
/// <c>application/json</c> with seven keys, and the other five describe a response the code cannot
/// produce at all. A client generated from it would have had no type for the field it must branch
/// on, and five branches for answers that never arrive.
/// </para>
/// <para>
/// These read the COMMITTED file rather than a live server on purpose: the committed file is what
/// downstream generation consumes, so it is the artefact whose truth matters here. Whether it still
/// matches a running API is a different question, answered by
/// <c>node scripts/openapi-spec.mjs check</c>.
/// </para>
/// <para>
/// Two of them read the code as well, by reflection, for registration's 403 on the public demo: it
/// is declared by an attribute that stands beside the marker that answers it, and neither the file
/// nor the generator can say that the two are still together.
/// </para>
/// </remarks>
public class PublishedErrorContractTests
{
    /// <summary>
    /// A response that cannot occur is as false as a body that is not declared, so both are refused.
    /// These three are the statuses an endpoint reaches through the paths that NAME a reason: the JWT
    /// <c>OnChallenge</c>/<c>OnForbidden</c> events for 401 and 403, and <c>AppExceptionHandler</c>
    /// for 404 (a <c>NotFoundException</c> is an <c>AppException</c>). All three write both
    /// <c>errorCode</c> and <c>traceId</c>.
    ///
    /// Deliberately NOT here: 400 and 500. <c>ValidationExceptionHandler</c> writes an
    /// <c>errors</c> dictionary and no <c>errorCode</c>, and <c>GlobalExceptionHandler</c> writes
    /// neither — so a guard demanding a ProblemDetails body on those would be the same over-claim
    /// this file exists to catch, pointed at ourselves.
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
            because: "the refusals this guard scans — 401, 403, 404 — all carry one, and it is the "
                     + "field clients branch on. Not EVERY error does, which is what the next test pins");
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

    /// <summary>
    /// The four keyed money operations: the only ones whose 503 can say <c>applied: false</c>,
    /// because only they hold an idempotency claim whose fate the API knows (ADR-0058).
    /// </summary>
    private static readonly string[] MoneyOperations =
    [
        "POST /api/transactions/deposit",
        "POST /api/transactions/withdraw",
        "POST /api/transfers",
        "POST /api/transfers/internal",
    ];

    private static IEnumerable<(string Operation, JsonElement Value)> Operations(JsonElement document)
    {
        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (HttpMethods.Contains(operation.Name))
                {
                    yield return ($"{operation.Name.ToUpperInvariant()} {path.Name}", operation.Value);
                }
            }
        }
    }

    /// <summary>The schema a 503 declares, or null when the operation declares no 503 with a body.</summary>
    private static JsonElement? OutageSchema(JsonElement operation)
    {
        if (!operation.TryGetProperty("responses", out var responses)
            || !responses.TryGetProperty("503", out var outage)
            || !outage.TryGetProperty("content", out var content))
        {
            return null;
        }

        foreach (var mediaType in content.EnumerateObject())
        {
            if (mediaType.Value.TryGetProperty("schema", out var schema))
            {
                return schema;
            }
        }

        return null;
    }

    /// <summary>Every property a schema declares, through <c>$ref</c> and <c>allOf</c>.</summary>
    private static HashSet<string> PropertiesOf(JsonElement document, JsonElement schema)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var component = reference.GetString()!.Split('/')[^1];
            schema = document.GetProperty("components").GetProperty("schemas").GetProperty(component);
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            names.UnionWith(properties.EnumerateObject().Select(p => p.Name));
        }

        if (schema.TryGetProperty("allOf", out var parts))
        {
            foreach (var part in parts.EnumerateArray())
            {
                names.UnionWith(PropertiesOf(document, part));
            }
        }

        return names;
    }

    [Fact]
    public void Every_operation_declares_the_outage_503()
    {
        // ADR-0058: any request can meet the database being down or the request deadline, and the
        // answer is a 503 with errorCode SERVICE_UNAVAILABLE. Before, one operation declared it
        // (revoke, whose 503 predates this), so a generated client had no type for the other 29.
        var document = Document();
        var operations = Operations(document).ToList();
        operations.Should().HaveCount(31, "the API's 31 operations; fewer means the scan broke");

        var missing = operations.Where(o => OutageSchema(o.Value) is null).Select(o => o.Operation).ToList();

        missing.Should().BeEmpty(
            "every operation can answer the outage 503; {0} of {1} declare none: {2}",
            missing.Count, operations.Count, string.Join(", ", missing));
    }

    [Fact]
    public void The_demo_claim_declares_the_404_of_a_deployment_that_is_not_the_demo()
    {
        // The document is the same for every deployment, and on one with the demo off this
        // operation answers nothing but 404: the framework's own, as for a path with no route.
        // Measured through the test host (DemoModeEndpointTests): application/problem+json with
        // type, title, status and traceId, and no errorCode.
        var paths = Document().GetProperty("paths");
        paths.TryGetProperty("/api/auth/demo/claim", out var path).Should().BeTrue("the claim is published");
        path.TryGetProperty("post", out var claim).Should().BeTrue();
        var responses = claim.GetProperty("responses");

        responses.EnumerateObject().Select(r => r.Name).Should().BeEquivalentTo(
            ["200", "400", "404", "415", "429", "503"],
            "what a claim can answer: the copy, a body it cannot read, the demo off, a body that is "
            + "not JSON, a refusal for now, and the outage");

        var notFound = responses.GetProperty("404");
        notFound.GetProperty("description").GetString().Should().Contain("the demo is off");
        var content = notFound.GetProperty("content");
        content.EnumerateObject().Select(m => m.Name).Should().Equal(
            ["application/problem+json"], "this 404 is never the application's own application/json refusal");
        content.GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/ProblemDetails");

        // Anonymous: the visitor has no account yet. An empty requirement, not an absent one.
        claim.GetProperty("security").EnumerateArray().Should().ContainSingle()
            .Which.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void Registration_declares_the_403_of_the_public_demo()
    {
        // The document is the same for every deployment, and on the public demo this operation
        // answers the BFF's own client nothing but 403: registration is closed there. Measured
        // through the test host (DemoModeEndpointTests): the application's own refusal,
        // application/json with errorCode REGISTRATION_CLOSED, one sentence and a traceId.
        var register = Document().GetProperty("paths").GetProperty("/api/auth/register").GetProperty("post");
        var responses = register.GetProperty("responses");

        responses.EnumerateObject().Select(r => r.Name).Should().BeEquivalentTo(
            ["201", "400", "403", "409", "415", "503"],
            "what a registration can answer: the user, a body it cannot read, the demo, a "
            + "duplicate, a body that is not JSON, and the outage");

        var content = responses.GetProperty("403").GetProperty("content");
        content.EnumerateObject().Select(m => m.Name).Should().Equal(
            ["application/json"], "the 403 is the application's refusal, written by its exception handler");
        content.GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/ProblemDetails");

        // The code a client branches on, named where the operation is described.
        register.GetProperty("description").GetString().Should().Contain("REGISTRATION_CLOSED");

        // And the answer the file declares is one the code gives: the action carries the marker
        // the demo closes it by. Measured before this assertion was here: with the marker taken
        // off the action, this class and CommittedOpenApiDocumentTests passed, 12 of 12, on a
        // file declaring a 403 that no deployment would answer.
        typeof(AuthController).GetMethod(nameof(AuthController.Register))!
            .GetCustomAttribute<ClosedInDemoAttribute>()
            .Should().NotBeNull("the 403 declared for registration is the one this marker makes the demo answer");
    }

    [Fact]
    public void Every_action_the_demo_closes_declares_the_403_it_answers_there()
    {
        // The other way round, from the code. The 403 is declared by an attribute written beside
        // the marker and not derived from it, so an action can gain the marker and answer a 403
        // the document does not have.
        var closed = typeof(AuthController).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(action => action.GetCustomAttribute<ClosedInDemoAttribute>() is not null)
            .ToList();

        using var scope = new AssertionScope();
        closed.Select(action => $"{action.DeclaringType!.Name}.{action.Name}").Should().Equal(
            ["AuthController.Register"],
            "registration is the one endpoint the demo closes, and a scan that finds none checks nothing");
        closed.Should().OnlyContain(
            action => action.GetCustomAttributes<ProducesResponseTypeAttribute>()
                .Any(declared => declared.StatusCode == StatusCodes.Status403Forbidden),
            "an endpoint the demo closes declares the 403 it answers there");
    }

    [Fact]
    public void ProblemDetails_declares_retryAfterSeconds_as_an_optional_integer()
    {
        var schema = Document().GetProperty("components").GetProperty("schemas").GetProperty("ProblemDetails");

        schema.GetProperty("properties").TryGetProperty("retryAfterSeconds", out var retryAfter).Should().BeTrue(
            "the SPA reads when to retry from the body: the Retry-After header does not always reach it");
        retryAfter.GetProperty("type").ToString().Should().Contain("integer");

        var required = schema.TryGetProperty("required", out var r)
            ? r.EnumerateArray().Select(e => e.GetString()).ToArray()
            : [];
        required.Should().NotContain("retryAfterSeconds", "most refusals carry none");
    }

    // Until 2026-10-01 this was Applied_is_declared_on_the_four_money_503s_and_nowhere_else. It reads
    // 503 schemas only, and since that date the four money 409s declare `applied` too (as true: the
    // tests below), so "nowhere else" had stopped being what it checks.
    [Fact]
    public void Applied_is_declared_on_the_503s_of_the_four_money_operations_only()
    {
        var document = Document();

        PropertiesOf(document, document.GetProperty("components").GetProperty("schemas").GetProperty("ProblemDetails"))
            .Should().NotContain("applied", "the shared component would claim it for 26 operations that never send it");

        var declaring = Operations(document)
            .Where(o => OutageSchema(o.Value) is { } schema && PropertiesOf(document, schema).Contains("applied"))
            .Select(o => o.Operation)
            .ToList();

        declaring.Should().BeEquivalentTo(
            MoneyOperations,
            "only a keyed money operation knows whether its claim was its own and no commit started");
    }

    /// <summary>The schema a 409 declares, or null when the operation declares no 409 with a body.</summary>
    private static JsonElement? ConflictSchema(JsonElement operation) => ResponseSchema(operation, "409");

    private static JsonElement? ResponseSchema(JsonElement operation, string status)
    {
        if (!operation.TryGetProperty("responses", out var responses)
            || !responses.TryGetProperty(status, out var response)
            || !response.TryGetProperty("content", out var content))
        {
            return null;
        }

        foreach (var mediaType in content.EnumerateObject())
        {
            if (mediaType.Value.TryGetProperty("schema", out var schema))
            {
                return schema;
            }
        }

        return null;
    }

    /// <summary>The schema of one property, through <c>$ref</c> and <c>allOf</c>, or null when it is not declared.</summary>
    private static JsonElement? PropertyOf(JsonElement document, JsonElement schema, string name)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var component = reference.GetString()!.Split('/')[^1];
            schema = document.GetProperty("components").GetProperty("schemas").GetProperty(component);
        }

        if (schema.TryGetProperty("properties", out var properties)
            && properties.TryGetProperty(name, out var property))
        {
            return property;
        }

        if (schema.TryGetProperty("allOf", out var parts))
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (PropertyOf(document, part, name) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>The values a property's <c>enum</c> allows, as JSON text; empty when it declares none.</summary>
    private static string[] EnumOf(JsonElement property) =>
        property.TryGetProperty("enum", out var values)
            ? values.EnumerateArray().Select(v => v.GetRawText()).ToArray()
            : [];

    [Fact]
    public void Applied_is_declared_as_true_on_the_409s_of_the_four_money_operations_and_on_no_other_409()
    {
        // The 409 IDEMPOTENCY_RESULT_UNKNOWN says `applied: true` when the API read the key's record
        // from the database as executed. Only the four keyed money operations hold such a record.
        // The member is true or absent: `false` on a 409 would say "nothing moved" where nothing is
        // known, so the declared values are exactly [true], as the 503's are exactly [false].
        var document = Document();
        var conflicts = Operations(document)
            .Select(o => (o.Operation, Schema: ConflictSchema(o.Value)))
            .Where(o => o.Schema is not null)
            .Select(o => (o.Operation, Schema: o.Schema!.Value))
            .ToList();

        conflicts.Select(o => o.Operation).Should().Contain(MoneyOperations);
        conflicts.Should().HaveCountGreaterThan(
            MoneyOperations.Length,
            "other operations answer a 409 of their own; without one in the scan, 'no other 409' proves nothing");

        foreach (var (operation, value) in Operations(document).Where(o => MoneyOperations.Contains(o.Operation)))
        {
            var outage = OutageSchema(value);
            outage.Should().NotBeNull("{0} declares the outage 503", operation);
            var applied = PropertyOf(document, outage!.Value, "applied");
            applied.Should().NotBeNull("{0}: the money 503 declares applied", operation);
            EnumOf(applied!.Value).Should().Equal(new[] { "false" }, "{0}: a 503 never says true", operation);
        }

        var declaring = conflicts
            .Where(o => PropertiesOf(document, o.Schema).Contains("applied"))
            .Select(o => o.Operation)
            .ToList();

        declaring.Should().BeEquivalentTo(
            MoneyOperations,
            "the four money 409s say whether the operation was applied, and no other 409 holds a claim to say it about");

        foreach (var (operation, schema) in conflicts.Where(o => MoneyOperations.Contains(o.Operation)))
        {
            var applied = PropertyOf(document, schema, "applied")!.Value;
            applied.GetProperty("type").ToString().Should().Contain("boolean", "{0}", operation);
            EnumOf(applied).Should().Equal(
                new[] { "true" }, "{0}: on a 409, applied is true or absent, never false", operation);
        }
    }

    [Fact]
    public void The_money_409_describes_both_codes_and_what_applied_true_means()
    {
        var document = Document();
        var described = 0;

        foreach (var (operation, value) in Operations(document).Where(o => MoneyOperations.Contains(o.Operation)))
        {
            var description = value.GetProperty("responses").GetProperty("409").GetProperty("description").GetString();
            described++;

            description.Should().Contain("IDEMPOTENCY_IN_FLIGHT", "{0}", operation);
            description.Should().Contain("IDEMPOTENCY_RESULT_UNKNOWN", "{0}", operation);
            description.Should().Contain(
                "applied: true", "{0}: a client must be told what the member means", operation);
            description.Should().Contain(
                "do not send it again",
                "{0}: the old text ended in 'verify', which for a committed operation invites a second payment",
                operation);
        }

        described.Should().Be(MoneyOperations.Length, "all four money operations were read");
    }

    /// <summary>Every schema a response declares: one for each media type that carries one.</summary>
    private static IEnumerable<JsonElement> SchemasOf(JsonElement response)
    {
        if (!response.TryGetProperty("content", out var content))
        {
            yield break;
        }

        foreach (var mediaType in content.EnumerateObject())
        {
            if (mediaType.Value.TryGetProperty("schema", out var schema))
            {
                yield return schema;
            }
        }
    }

    [Fact]
    public void Applied_is_declared_on_no_response_but_the_409_and_the_503_of_the_four_money_operations()
    {
        // The two tests above read 409 and 503 schemas. A money operation's 422 and 413 are built
        // by the same helper as its 409, one argument apart, and neither ever carries the member:
        // declared there it would be published for answers that never send it, and nothing that
        // reads only 409s and 503s would notice.
        //
        // Every media type of every response is read here. The readers above stop at the first
        // one a response declares, and a response can declare two (eight 404s did on 2026-10-01):
        // a member published under the second is published all the same. The declared values are
        // read with it, so a second media type cannot say false on a 409 or true on a 503 either.
        var document = Document();
        var declaring = new HashSet<string>(StringComparer.Ordinal);
        var scanned = 0;

        foreach (var (operation, value) in Operations(document))
        {
            if (!value.TryGetProperty("responses", out var responses)) continue;

            foreach (var response in responses.EnumerateObject())
            {
                scanned++;
                foreach (var schema in SchemasOf(response.Value))
                {
                    if (PropertyOf(document, schema, "applied") is { } applied)
                    {
                        declaring.Add($"{operation} {response.Name} [{string.Join(", ", EnumOf(applied))}]");
                    }
                }
            }
        }

        scanned.Should().BeGreaterThan(MinimumResponsesScanned, because:
            "a scan that reads nothing would report that no other response declares it");

        declaring.Should().BeEquivalentTo(
            MoneyOperations.SelectMany(o => new[] { $"{o} 409 [true]", $"{o} 503 [false]" }),
            "applied is sent as true on the 409 and as false on the 503 of a keyed money operation, "
            + "and on nothing else, whichever media type declares the response");
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
