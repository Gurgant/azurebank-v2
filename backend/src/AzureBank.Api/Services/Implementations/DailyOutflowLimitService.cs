using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Services.Implementations;

/// <inheritdoc cref="IDailyOutflowLimit" />
public sealed class DailyOutflowLimitService : IDailyOutflowLimit
{
    private readonly AzureBankDbContext _context;
    private readonly DailyLimitOptions _options;
    private readonly TimeProvider _clock;

    /// <param name="context">
    /// The request's own context, shared with the transfer (see the interface).
    /// </param>
    /// <param name="options">The ceiling, validated at startup.</param>
    /// <param name="clock">
    /// NOT optional, unlike the context's parameter: this class exists to compute a window over
    /// rows the context stamped, so the app must fail to resolve rather than quietly run on a
    /// second clock. Nothing in this class reads the system clock directly, and
    /// <c>LedgerClockHygieneTests</c> keeps it so (it scans for the token, comments included).
    /// </param>
    public DailyOutflowLimitService(
        AzureBankDbContext context,
        IOptions<DailyLimitOptions> options,
        TimeProvider clock)
    {
        _context = context;
        _options = options.Value;
        _clock = clock;
    }

    /// <inheritdoc />
    public decimal Limit => _options.Amount;

    /// <inheritdoc />
    public (DateTime Start, DateTime ResetsAt) Window()
    {
        // UTC calendar day, as policy (ADR-0050 D1): CreatedAt is UTC from the context clock, no
        // user time zone exists, and a rolling window belongs to the umbrella entry by name.
        var start = _clock.GetUtcNow().UtcDateTime.Date;
        return (start, start.AddDays(1));
    }

    /// <inheritdoc />
    public Task<decimal> UsedAsync(Guid userId, CancellationToken cancellationToken = default)
        => UsedAsync(userId, Window(), cancellationToken);

    /*
      THE PREDICATE, and why each term is there.

      Type == TransferOut && RecipientAzureTag != null is the ledger's only discriminator between an
      external transfer and an internal one: both kinds write a TransferOut row, and only the
      external constructor sets RecipientAzureTag (TransferService, the two `new Transaction`
      blocks). Measured on AzureBankDev 2026-09-07: TransferOut rows with the tag SET — 20, every
      one to another user's account; NULL — 10, every one to the same user. Internal transfers,
      withdrawals, deposits and TransferIn are therefore excluded by construction, and
      DailyOutflowLimitServiceTests pins each exclusion, because a future internal row that set a
      tag would silently start counting.

      Status == Completed mirrors GetSummaryAsync at zero cost; every writer sets Completed today
      (measured 2026-09-07: "statuses ever written in the table: Completed 207"), so "settled" and
      "committed" coincide until something writes Pending.

      Account.UserId is the join — a Transaction carries no UserId — and there is deliberately NO
      `!Account.IsDeleted` here, which is why IgnoreQueryFilters() is on the query: the global
      soft-delete filter on Account applies through the navigation, and copying GetSummaryAsync's
      exclusion would let a user regain the day's headroom by draining an account through counted
      outflows and closing it (closure needs a zero balance, NON_ZERO_BALANCE). A closed account's
      earlier outflows are real.
    */
    private Task<decimal> UsedAsync(
        Guid userId, (DateTime Start, DateTime ResetsAt) window, CancellationToken cancellationToken)
        => _context.Transactions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.Account.UserId == userId
                        && t.Type == TransactionType.TransferOut
                        && t.RecipientAzureTag != null
                        && t.Status == TransactionStatus.Completed
                        && t.CreatedAt >= window.Start
                        && t.CreatedAt < window.ResetsAt)
            .SumAsync(t => t.Amount, cancellationToken);

    /*
      `used + requested > limit`, on COMMITTED rows, and the placement is part of the comparison.
      The in-transaction call in TransferService runs BEFORE this request's rows are built, so the
      request's own amount is not in the sum and must be added here. A future refactor that moves
      that call AFTER the first SaveChangesAsync — where the request's own TransferOut row is
      already on the connection and IS in the sum — must switch it to `used > limit`, or every
      transfer that lands exactly on the limit is refused and the bound double-counts. The
      boundary test (`used + amount == limit` passes, `+ 0.01` refuses) is what would catch the
      copy-paste.
    */
    /// <inheritdoc />
    public async Task AssertCanMoveAsync(
        Guid userId, decimal amount, CancellationToken cancellationToken = default)
    {
        var window = Window();
        var used = await UsedAsync(userId, window, cancellationToken);

        if (used + amount > _options.Amount)
        {
            throw new DailyLimitExceededException(_options.Amount, used, amount, window.ResetsAt);
        }
    }
}
