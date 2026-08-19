using System.Diagnostics;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Api.Services.Implementations;

/// <inheritdoc cref="IAuditService"/>
public class AuditService : IAuditService
{
    private readonly AzureBankDbContext _context;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuditService> _logger;

    public AuditService(
        AzureBankDbContext context,
        IServiceScopeFactory scopeFactory,
        ILogger<AuditService> logger,
        TimeProvider? clock = null)
    {
        _context = context;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public void Record(
        string securityEvent,
        AuditOutcome outcome,
        Guid? actorUserId = null,
        string? subjectType = null,
        Guid? subjectId = null,
        string? detail = null)
    {
        if (outcome is AuditOutcome.Refused or AuditOutcome.MitigationFailed)
        {
            /*
              Refused loudly rather than accepted quietly, because the failure it prevents is silent.
              A refusal enlisted here rides a transaction that is usually about to roll back —
              measured, AuthService's two duplicate-registration races log from inside
              strategy.ExecuteInTransactionAsync and that transaction rolls back with the 409 — so
              the row would be written and then deleted, leaving an audit trail that shows only the
              successes. Use RecordRefusalAsync.
            */
            throw new ArgumentException(
                $"{outcome} must be written with RecordRefusalAsync: enlisting it in the current "
                + "transaction would let the rollback that refuses the operation also erase the "
                + "record of the refusal.",
                nameof(outcome));
        }

        // Add, never save — the contract of IIdempotencyService.MarkExecutedPending, and what makes
        // the row atomic with the business change at every call site without new machinery.
        _context.AuditEvents.Add(Build(securityEvent, outcome, actorUserId, subjectType, subjectId, detail));
    }

    /// <inheritdoc />
    public async Task RecordRefusalAsync(
        string securityEvent,
        AuditOutcome outcome,
        Guid? actorUserId = null,
        string? subjectType = null,
        Guid? subjectId = null,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        /*
          THE COMPLEMENT of Record's guard, so the pair cannot be swapped by accident in EITHER
          direction. Record throws on Refused and MitigationFailed because those must not be tied to
          a transaction that is about to roll back; this one throws on the rest, because a Succeeded
          row committed on its own connection would assert that something happened while the
          business transaction carrying it may still abandon it — D1 inverted, and a worse lie than
          a missing row. The guard existed on one side only until CodeRabbit pointed at the gap.
        */
        if (outcome is not (AuditOutcome.Refused or AuditOutcome.MitigationFailed))
        {
            throw new ArgumentException(
                $"{outcome} must be written with Record, so it stays atomic with the business "
                    + "transaction it describes (ADR-0044 D1).",
                nameof(outcome));
        }

        /*
          A SEPARATE scope, and therefore a separate DbContext and connection. The whole point is to
          be outside the caller's transaction: this row must survive the rollback of whatever was
          refused. Using the request-scoped context would enlist it in exactly the transaction we are
          trying to escape.
        */
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        context.AuditEvents.Add(Build(securityEvent, outcome, actorUserId, subjectType, subjectId, detail));
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogDebug(
            "Audit refusal {AuditedEvent} recorded out-of-band for actor {ActorUserId}",
            securityEvent, actorUserId);
    }

    private AuditEvent Build(
        string securityEvent,
        AuditOutcome outcome,
        Guid? actorUserId,
        string? subjectType,
        Guid? subjectId,
        string? detail) => new()
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = _clock.GetUtcNow().UtcDateTime,
            Event = securityEvent,
            Outcome = outcome,
            ActorUserId = actorUserId,
            SubjectType = subjectType,
            SubjectId = subjectId,
            // The bare 32-hex form, matching what GlobalExceptionHandler and the JWT events already
            // put in every ProblemDetails, so a user's error message joins to this row by eye.
            TraceId = Activity.Current?.TraceId.ToString(),
            Detail = detail,
            // Filled by AuditChain inside the SaveChanges funnel — it is the only place the tail can
            // be read under a lock inside the same transaction as the insert.
            RowHash = string.Empty,
        };
}
