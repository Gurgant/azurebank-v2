using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzureBank.Api.HealthChecks;

/// <summary>
/// Writes a readiness body that says WHICH check failed, rather than only that something did.
/// </summary>
/// <remarks>
/// <para>
/// The default writer emits the aggregate word — <c>Unhealthy</c> — as <c>text/plain</c> and nothing
/// else. That is enough for a load balancer, which only reads the status code, and useless for the
/// operator the runbook is written for: <c>docs/runbooks/audit-chain-unavailable.md</c> opens by
/// asking them to tell an unhealthy <c>audit-chain</c> apart from an unhealthy <c>database</c>,
/// because the second is a database outage and the first is not. A one-word body cannot answer that,
/// so the runbook's first step was unanswerable until this existed.
/// </para>
/// <para>
/// <b>Descriptions only — never the exception.</b> <see cref="HealthReportEntry.Exception"/> on a
/// failed database probe carries the connection string, the server name and the login that failed.
/// This endpoint takes no credential, so everything written here is written to anyone who asks. The
/// descriptions are ours and deliberately name a consequence rather than an internal; the cause
/// stays in the log, where reading it takes access.
/// </para>
/// </remarks>
public static class HealthCheckResponse
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
            }),
        }));
    }
}
