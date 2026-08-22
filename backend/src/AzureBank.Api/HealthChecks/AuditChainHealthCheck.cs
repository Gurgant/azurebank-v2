using System.Globalization;
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
    /*
      READS THE TAIL **AND** ASKS WHETHER THIS PRINCIPAL COULD WRITE ONE, because reading was never
      the capability that matters. D1 refuses a money movement when the audit row cannot be WRITTEN;
      a login with SELECT but no INSERT passes a read-only probe while every deposit, withdrawal and
      transfer fails. Measured on SQL Server with a user granted SELECT and denied INSERT: the read
      probe succeeded — the check would have reported Healthy — while the write came back "The INSERT
      permission was denied on the object 'AuditEvents'". Readiness would have kept an instance in
      rotation that could not move a penny.

      HAS_PERMS_BY_NAME answers for the CURRENT security context, honouring role membership and DENY,
      and it neither writes nor locks. The alternative — insert a row and roll it back — would put a
      write into the audit table on every probe cycle and take the tail lock to do it.

      The object name is resolved rather than hardcoded so a non-dbo schema cannot silently make the
      permission question ask about a table that does not exist.
    */
    private const string ProbeSql = """
        DECLARE @tail bigint;
        SELECT TOP 1 @tail = [Sequence] FROM [AuditEvents] WITH (READUNCOMMITTED) ORDER BY [Sequence] DESC;

        DECLARE @object sysname =
            QUOTENAME(OBJECT_SCHEMA_NAME(OBJECT_ID('AuditEvents'))) + N'.' +
            QUOTENAME(OBJECT_NAME(OBJECT_ID('AuditEvents')));

        SELECT CAST(CASE
            WHEN DATABASEPROPERTYEX(DB_NAME(), 'Updateability') <> 'READ_WRITE' THEN 2
            WHEN ISNULL(HAS_PERMS_BY_NAME(@object, 'OBJECT', 'INSERT'), 0) = 1 THEN 1
            ELSE 0
        END AS int);
        """;

    /// <summary>What the probe found: 1 permitted, 0 permission refused, 2 database is read-only.</summary>
    private const int AppendPermitted = 1;
    private const int AppendRefused = 0;
    private const int DatabaseReadOnly = 2;

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
            /*
              Readable and useless is the state to catch here. The store answers, so nothing throws
              and nothing looks broken — but D1 refuses every money movement, because the row it must
              write alongside them cannot be written. Unhealthy for the same reason as an outage:
              this instance cannot do the thing it exists to do.
            */
            return await AppendabilityAsync(cancellationToken) switch
            {
                /*
                  "PERMITTED", not "writable", and the distinction is measured rather than pedantic.
                  HAS_PERMS_BY_NAME answers a question about permission METADATA, not about whether
                  an INSERT would succeed: on this instance it returns 1 for [sys].[objects], where
                  an insert is categorically impossible ("Ad hoc updates to system catalogs are not
                  allowed"). So a Healthy verdict here means the two things actually checked — the
                  table is legible, and nothing in the permission graph forbids appending to it.
                  Claiming "writable" would assert a capability no lock-free probe can establish.
                */
                AppendPermitted => HealthCheckResult.Healthy(
                    "audit store readable, and appending to it is permitted for this login"),

                // Reported separately because the fix has nothing to do with permissions, and a
                // message about GRANTs would send an operator to the wrong runbook step entirely.
                DatabaseReadOnly => HealthCheckResult.Unhealthy(
                    "audit store readable but NOT writable — the database is not READ_WRITE, so "
                    + "money movements will be refused (ADR-0044 D1)"),

                _ => HealthCheckResult.Unhealthy(
                    "audit store readable but NOT writable — money movements will be refused "
                    + "(ADR-0044 D1). Check INSERT permission on AuditEvents for this login"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            /*
              THE SAME ESCAPE AuditChain HAS, which this check was missing. The token here carries
              two things that are not an unreadable store: the caller giving up (Kubernetes' default
              probe timeout is one second, well under the budget installed for this registration),
              and the registration budget itself expiring.

              Swallowing either would report "audit store unreadable — money movements will be
              refused", which sends an operator to a runbook that enumerates outages, missing tables
              and broken indexes — none of which is what happened. Letting it propagate lets the
              framework classify it: measured, DefaultHealthCheckService reports Unhealthy with "A
              timeout occurred while running check.", which names the real cause.
            */
            throw;
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

    /// <summary>
    /// One round trip: reads the tail, and returns whether this principal may INSERT into it.
    /// </summary>
    /// <remarks>
    /// Raw ADO rather than <c>SqlQueryRaw</c> because this is a multi-statement batch, and EF would
    /// compose a scalar query into <c>SELECT ... FROM (batch)</c>, which is not valid around one.
    /// <c>ExecuteScalar</c> returns the first column of the first ROW-RETURNING statement, and the
    /// tail read assigns into a variable rather than returning rows — so the permission answer is
    /// what comes back, while the read still proves the table is there and legible.
    /// </remarks>
    private async Task<int> AppendabilityAsync(CancellationToken cancellationToken)
    {
        var opened = false;
        try
        {
            await _context.Database.OpenConnectionAsync(cancellationToken);
            opened = true;

            await using var command = _context.Database.GetDbConnection().CreateCommand();
            command.CommandText = ProbeSql;

            var verdict = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(verdict, CultureInfo.InvariantCulture);
        }
        finally
        {
            // Only if WE opened it — closing a connection this check never opened would take it out
            // from under whoever did.
            if (opened)
            {
                await _context.Database.CloseConnectionAsync();
            }
        }
    }
}
