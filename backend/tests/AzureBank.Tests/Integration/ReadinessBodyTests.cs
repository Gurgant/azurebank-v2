using System.Net;
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

        body.Should().Contain(
            "audit-chain",
            "the runbook tells an operator to read this body and identify the failing check by name");
        body.Should().Contain(
            "database",
            "and to tell it apart from the database check, which needs a different runbook entirely");

        /*
          Measured against the running API with the AuditEvents table renamed away: the unhealthy
          body carries description "audit store unreadable — money movements will be refused
          (ADR-0044 D1)" while database stayed Healthy. That direction is pinned by
          AuditChainHealthCheckTests; what is pinned HERE is that the endpoint publishes the
          per-check detail at all, which is the part the writer supplies.
        */
        body.Should().Contain(
            "\"description\":\"audit store",
            "the per-check description is what carries the meaning — a name with no description "
            + "would tell an operator which check failed but not what it means for the bank. "
            + "Matched on the prefix rather than the whole sentence because this factory runs the "
            + "InMemory provider, where the check reports 'not applicable' rather than 'readable' — "
            + "the wording is the provider's, the PUBLISHING of it is what this pins");
    }
}
