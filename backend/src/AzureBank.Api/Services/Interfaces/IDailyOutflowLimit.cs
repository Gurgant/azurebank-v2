namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// The day's ceiling on a user's outgoing external transfers (ADR-0050): one helper owning the
/// window, the sum and the assertion, so the three call sites in <c>TransferService</c> cannot
/// drift from each other — the same reason <c>ResolveExternalPayeeAsync</c> was extracted.
/// </summary>
/// <remarks>
/// Scoped and sharing the request's <c>AzureBankDbContext</c> on purpose: the authoritative call
/// runs INSIDE the transfer's transaction, after the per-user application lock is taken, and it
/// must read on the connection that holds the lock. The clock it reads the day from is the DI
/// <see cref="TimeProvider"/> — the same instance that stamps <c>CreatedAt</c> on the rows it sums.
/// </remarks>
public interface IDailyOutflowLimit
{
    /// <summary>The configured ceiling, as bound from <c>DailyLimit:Amount</c>.</summary>
    decimal Limit { get; }

    /// <summary>
    /// The current UTC calendar day: <c>Start</c> is 00:00:00Z today, <c>ResetsAt</c> is the next
    /// 00:00:00Z. Both are <c>DateTimeKind.Utc</c>.
    /// </summary>
    (DateTime Start, DateTime ResetsAt) Window();

    /// <summary>
    /// The sum of the user's COMPLETED external <c>TransferOut</c> rows in <see cref="Window"/>,
    /// across every account the user owns, closed ones included.
    /// </summary>
    Task<decimal> UsedAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws <see cref="AzureBank.Shared.Exceptions.DailyLimitExceededException"/> when
    /// <c>used + amount</c> would exceed <see cref="Limit"/>. Reads COMMITTED rows only, so it must
    /// run BEFORE the request's own rows exist on the connection — see the remarks on the service.
    /// </summary>
    Task AssertCanMoveAsync(Guid userId, decimal amount, CancellationToken cancellationToken = default);
}
