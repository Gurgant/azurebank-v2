using System.Net;
using System.Text.Json;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The readiness body names WHICH check failed — a claim the audit-chain runbook depends on.
/// </summary>
/// <remarks>
/// The default health-check writer emits the aggregate word (<c>Unhealthy</c>) as plain text and
/// nothing else. The runbook's first triage step is "is `database` unhealthy too, or only
/// `audit-chain`?" — one is a database outage and the other is not — and a one-word body cannot
/// answer it. This pins the shape so a future change to the endpoint cannot quietly turn the
/// runbook's opening step back into something an operator cannot carry out.
/// </remarks>
public class ReadinessBodyTests
{
    [Fact]
    public async Task ReadinessNamesEachCheck_NotJustTheAggregateStatus()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        /*
          PARSED, not substring-matched. The first version of this test asserted that the body
          CONTAINED "audit-chain" and "database", which would have passed on malformed JSON, or if
          the names moved out of the checks array into some other part of the document — and a
          monitoring tool reading this endpoint parses it, so the shape IS the contract.
        */
        using var document = JsonDocument.Parse(body);

        document.RootElement.GetProperty("status").GetString().Should().Be(
            "Healthy",
            "the aggregate is what a load balancer acts on, and it must survive alongside the detail");

        var checks = document.RootElement.GetProperty("checks").EnumerateArray().ToList();

        checks.Select(c => c.GetProperty("name").GetString()).Should().Contain(
            new[] { "audit-chain", "database" },
            "the runbook tells an operator to read this body, identify the failing check by name, and "
            + "tell audit-chain apart from database — the second needs a different runbook entirely");

        var audit = checks.Single(c => c.GetProperty("name").GetString() == "audit-chain");

        audit.GetProperty("status").GetString().Should().Be("Healthy");
        audit.GetProperty("description").GetString().Should().StartWith(
            "audit store",
            "the per-check description carries the meaning — a name with no description tells an "
            + "operator which check failed but not what it means for the bank. Matched on the prefix "
            + "because this factory runs the InMemory provider, where the check reports 'not "
            + "applicable' rather than 'readable': the wording is the provider's, the PUBLISHING of "
            + "it is what this pins. Measured on the real stack with AuditEvents renamed away, the "
            + "same field read 'audit store unreadable — money movements will be refused (ADR-0044 D1)'");
    }
}
