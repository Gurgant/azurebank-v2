using AzureBank.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzureBank.Api.HealthChecks;

/// <summary>
/// Reports whether the audit store can be read at all — which, since ADR-0044 D1, decides whether
/// this instance can move money.
/// </summary>
/// <remarks>
/// <para>
/// D1 makes an audit row atomic with the movement it describes, so an unreadable or unwritable
/// <c>AuditEvents</c> table does not degrade the bank, it stops it. That is the accepted trade. What
/// was NOT acceptable is finding out from a customer: every source that endorses fail-closed pairs
/// it with a way to see the failure coming, and this instance had none.
/// </para>
/// <para>
/// <b>DELIBERATELY LOCK-FREE, and this is the part worth reading.</b> The obvious probe — read the
/// chain tail the way <c>AuditChain</c> does — would take <c>UPDLOCK, HOLDLOCK</c> on the one row
/// every money movement queues behind. A readiness check that runs every few seconds would then
/// contend with the money path forever, to report on contention. The probe therefore reads with
/// <c>READUNCOMMITTED</c>: it takes no lock and waits on none.
/// </para>
/// <para>
/// <b>What that means it does and does not detect.</b> It detects the case that matters most and is
/// least visible — the audit store unreachable, the table gone, the database down — where every
/// movement will fail. It does NOT detect a tail that is merely locked by a slow writer, because
/// seeing that would require joining the queue it is reporting on. That case surfaces instead
/// through <c>SecurityEvents.AuditChainUnavailable</c>, which <c>AuditChain</c> logs when its bounded
/// wait expires. Two instruments, two questions; neither pretends to answer the other's.
/// </para>
/// </remarks>
public sealed class AuditChainHealthCheck : IHealthCheck
{
    private const string ProbeSql =
        "SELECT TOP 1 [Sequence] FROM [AuditEvents] WITH (READUNCOMMITTED) ORDER BY [Sequence] DESC";

    private readonly AzureBankDbContext _context;

    public AuditChainHealthCheck(AzureBankDbContext context) => _context = context;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_context.Database.IsRelational())
        {
            // The InMemory provider has no SQL to send and no store to be unreachable. Reporting
            // healthy is honest here rather than convenient: there is nothing this check can fail on.
            return HealthCheckResult.Healthy("audit store not applicable on this provider");
        }

        try
        {
            await _context.Database.ExecuteSqlRawAsync(ProbeSql, cancellationToken);
            return HealthCheckResult.Healthy("audit store readable");
        }
        catch (Exception ex)
        {
            /*
              Unhealthy rather than Degraded, and the choice follows D1 rather than taste: if this
              instance cannot reach the audit store it cannot move money, so it is not serving a
              reduced service — it is not serving the one that matters. Saying "degraded" would keep
              it in a load balancer's rotation.
            */
            return HealthCheckResult.Unhealthy(
                "audit store unreadable — money movements will be refused (ADR-0044 D1)", ex);
        }
    }
}
