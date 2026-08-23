using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureBank.Infrastructure.Data;

/// <summary>
/// Chains each <see cref="AuditEvent"/> to the one before it, at the moment it is written
/// (ADR-0044). Called from <see cref="AzureBankDbContext"/>'s SaveChanges funnels.
/// </summary>
public interface IAuditChain
{
    /// <summary>Fills the chain fields of every audit row added in this unit of work.</summary>
    Task ApplyAsync(DbContext context, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ApplyAsync"/>
    void Apply(DbContext context);

    /// <summary>
    /// Recomputes every row's hash in <c>Sequence</c> order and checks that each links to the one
    /// before it. This is what makes the chain a control rather than a decoration — a hash nobody
    /// ever verifies proves nothing.
    /// </summary>
    Task<AuditChainVerification> VerifyAsync(DbContext context, CancellationToken cancellationToken = default);
}

/// <summary>The result of walking the chain. A count as well as a verdict, deliberately.</summary>
/// <param name="Verified">Rows read and checked.</param>
/// <param name="FirstBrokenSequence">
/// The <c>Sequence</c> of the first row that failed, or null if none did.
/// </param>
/// <param name="Reason">Why it failed, in words an operator can act on. Null when intact.</param>
/// <remarks>
/// <see cref="Verified"/> exists so a caller can enforce a LIVENESS FLOOR. A verification that read
/// zero rows returns "intact", and that answer is worthless — the same failure mode
/// <c>SourceHygieneTests</c> was given a floor for after #119. Assert the count, not just the verdict.
/// </remarks>
/// <param name="LowestSequence">
/// The first and last sequence THIS WALK actually read, or null if it read nothing.
/// </param>
/// <param name="HighestSequence">See <paramref name="LowestSequence"/>.</param>
/// <remarks>
/// THE RANGE COMES FROM THE WALK ITSELF, and it is here rather than left to the caller because the
/// caller cannot get it right. Asking the database separately for MIN and MAX is two more statements
/// at two more instants: a row committed between the MAX and the walk is counted but falls outside
/// the range, so the tool could report 101 rows verified over a range ending at 100. The count and
/// the range exist to be compared with each other, so they have to come from one read.
/// </remarks>
public readonly record struct AuditChainVerification(
    long Verified,
    long? FirstBrokenSequence,
    string? Reason,
    long? LowestSequence = null,
    long? HighestSequence = null)
{
    /// <summary>True when every row read hashed and linked correctly.</summary>
    public bool IsIntact => FirstBrokenSequence is null;
}

/// <inheritdoc cref="IAuditChain"/>
/// <remarks>
/// <para>
/// WHY THIS RUNS INSIDE SaveChanges AND NOT INSIDE THE WRITER. The chain needs the tail of the table
/// read under a lock and the new row inserted with nobody slipping in between — one transaction.
/// <c>IAuditService.Record</c> cannot do that: it deliberately only calls <c>Add</c>, and
/// <c>AccountService.DeleteAccountAsync</c> has no explicit transaction at all, so a lock taken there
/// would be released before the insert and two concurrent writers would chain off the same tail. The
/// SaveChanges funnel is the only place a transaction can be guaranteed for EVERY call site, so
/// <c>AzureBankDbContext</c> opens one there when a save carries audit rows and the caller has not
/// opened one already.
/// </para>
/// <para>
/// CORRECTION, kept visible because the wrong version was written down first. This remark used to
/// claim the funnel was ALREADY inside "the transaction EF is using". It is not — EF opens its
/// implicit transaction inside <c>SaveChanges</c>, after this class has run, so the tail read
/// auto-committed and dropped its lock before the insert. Twenty-four concurrent writers on SQL
/// Server produced "Cannot insert duplicate key row ... IX_AuditEvents_Sequence. The duplicate key
/// value is (2)". The explicit transaction in the funnel is what actually makes the lock hold, and
/// <c>AuditChainSqlServerTests.ConcurrentWriters_DoNotForkTheChain</c> is what keeps it honest.
/// </para>
/// <para>
/// AND WHY NOT A SaveChangesInterceptor, which is the textbook answer. Measured on this repository:
/// <c>CustomWebApplicationFactory</c> removes <c>DbContextOptions</c> and
/// <c>IDbContextOptionsConfiguration</c> and rebuilds the registration itself, so an interceptor
/// attached in production wiring is simply absent under test. Every audit row would then be written
/// with an empty <see cref="AuditEvent.RowHash"/> — a required column — and the guarantee would exist
/// only where nobody looks. Living in the context class means every host gets it, because every host
/// constructs the same class.
/// </para>
/// <para>
/// SERIALISING COSTS NOTHING HERE, measured before it was chosen: eight concurrent writers × 100
/// inserts on LocalDB, timing only the SQL, gave 0.62 ms per insert unchained against 0.56 ms
/// chained — zero errors, zero deadlocks, and zero forks across 1,000 rows. The lock is held for
/// microseconds, so the queue never forms. That is a claim about eight writers on one machine, which
/// is what this application is; it is not a claim about a loaded server. Re-measured after the
/// transaction correction above, 24 concurrent writers on LocalDB: 24 rows, sequences 1..24, no
/// fork, no deadlock.
/// </para>
/// </remarks>
public sealed class AuditChain : IAuditChain
{
    private readonly IOptions<AuditOptions> _options;

    private readonly ILogger<AuditChain> _logger;

    public AuditChain(IOptions<AuditOptions> options, ILogger<AuditChain> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task ApplyAsync(DbContext context, CancellationToken cancellationToken = default)
    {
        var pending = Pending(context);
        if (pending.Count == 0)
        {
            return;
        }

        // Resolved BEFORE the read, so the same number bounds the statement and names itself in the
        // log line if it expires. Resolving it inside the catch would risk a second failure while
        // reporting the first.
        var timeoutSeconds = TailTimeoutSeconds;
        Link(pending, await ReadTailOrReportAsync(context, timeoutSeconds, pending.Count, cancellationToken));
    }

    public void Apply(DbContext context)
    {
        var pending = Pending(context);
        if (pending.Count == 0)
        {
            return;
        }

        /*
          THE SYNCHRONOUS FUNNEL GETS THE SAME TREATMENT, and it did not until a review asked why.
          Every money service saves asynchronously today, so this path is not on the money path in
          practice — but "not used today" is not a bound, and AzureBankDbContext.SaveChanges(bool)
          remains a public entry point that any audited unit of work can reach. An unbounded tail
          read here would hold the same global lock for the same thirty seconds, and be invisible
          because nothing logs it.
        */
        var timeoutSeconds = TailTimeoutSeconds;
        Link(pending, ReadTailOrReport(context, timeoutSeconds, pending.Count));
    }

    /// <inheritdoc cref="ReadTailOrReportAsync"/>
    private (long Sequence, string Hash)? ReadTailOrReport(
        DbContext context, int timeoutSeconds, int pendingRows)
    {
        try
        {
            return ReadTail(context, timeoutSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "SecurityEvent {SecurityEvent}: the audit chain tail could not be read within "
                    + "{TimeoutSeconds}s, so {PendingRows} pending audit row(s) and the action they "
                    + "describe are refused ON THIS ATTEMPT. A transient fault may be retried by the "
                    + "execution strategy and then succeed, so a single line is a blip and repetition "
                    + "is an outage",
                SecurityEvents.AuditChainUnavailable,
                timeoutSeconds,
                pendingRows);
            throw;
        }
    }

    /// <summary>
    /// The audit rows added in this unit of work. Ordered by <c>OccurredAt</c> and NOT by <c>Id</c>:
    /// Guid ordering in .NET is not creation order even for a UUIDv7, and SQL Server collates
    /// <c>uniqueidentifier</c> on a different byte order again — a trap this repository already
    /// records. Ties are resolved by the number this method's caller then assigns, so the order is
    /// recorded in the row rather than inferred from it.
    /// </summary>
    private static List<AuditEvent> Pending(DbContext context) =>
        context.ChangeTracker.Entries<AuditEvent>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .OrderBy(e => e.OccurredAt)
            .ToList();

    private void Link(List<AuditEvent> pending, (long Sequence, string Hash)? tail)
    {
        var previous = tail?.Hash;
        var sequence = tail?.Sequence ?? 0;

        foreach (var row in pending)
        {
            row.Sequence = ++sequence;
            row.PreviousHash = previous;
            row.RowHash = ComputeRowHash(row);
            previous = row.RowHash;
        }
    }

    /*
      UPDLOCK + HOLDLOCK, not a plain read, and both are load-bearing. UPDLOCK takes the update lock
      at read time so two writers cannot both read the same tail and then both append — the
      read-then-write race that produces a fork. HOLDLOCK keeps it to the end of the transaction, so
      nothing is inserted between our read and our insert. Drop either and the fork returns.
    */
    private const string TailSql =
        "SELECT TOP 1 [Sequence], [RowHash] FROM [AuditEvents] WITH (UPDLOCK, HOLDLOCK) ORDER BY [Sequence] DESC";

    /*
      THE WAIT IS BOUNDED HERE, AND ONLY HERE, and the reason is measured rather than assumed.

      The tail is read under UPDLOCK, HOLDLOCK, so the lock is global to AuditEvents and every
      audited save in the system queues on it. Stalling ONE tail read for three seconds delayed a
      deposit on a DIFFERENT account, by a DIFFERENT user, by 3,073-3,089 ms over three runs — the
      whole hold, proven by AuditChainContentionSqlServerTests. So a merely SLOW audit store degrades the whole bank, and
      until now the only bound was the global 30-second CommandTimeout, which covers the entire
      statement rather than the wait.

      Shortening it turns a long queue into a fast, loud refusal. That is not a softening of D1: a
      movement that cannot take the lock is still refused, still writes no money, and now fails in
      seconds instead of holding a connection and every other movement behind it for half a minute.

      RESTORED IN A finally, because the timeout lives on the DbContext and the DbContext is shared
      by everything else in the request. Leaving it set would quietly impose an audit-shaped deadline
      on every unrelated query the request makes afterwards.
    */
    private static async Task<(long Sequence, string Hash)?> ReadTailAsync(
        DbContext context, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (context.Database.IsRelational())
        {
            var restore = context.Database.GetCommandTimeout();
            context.Database.SetCommandTimeout(timeoutSeconds);
            try
            {
                var row = await context.Set<AuditEvent>()
                    .FromSqlRaw(TailSql)
                    .AsNoTracking()
                    .Select(e => new { e.Sequence, e.RowHash })
                    .FirstOrDefaultAsync(cancellationToken);
                return row is null ? null : (row.Sequence, row.RowHash);
            }
            finally
            {
                context.Database.SetCommandTimeout(restore);
            }
        }

        var inMemory = await NonRelationalTail(context).FirstOrDefaultAsync(cancellationToken);
        return inMemory is null ? null : (inMemory.Sequence, inMemory.RowHash);
    }

    /*
      SAY IT OUT LOUD, THEN LET IT FAIL. D1 is unchanged — the exception goes on and the movement does
      not happen — but an operator now learns that the audit chain is what stopped it, instead of
      reading an anonymous 500 and guessing.

      A LOG LINE AND NOT AN AUDIT ROW, which is the whole point and is worth stating where a reader
      will hit it: RecordRefusalAsync writes to AuditEvents, so it takes the very lock that just
      failed. For a chain failure there is no in-band way to report a chain failure.
    */
    private async Task<(long Sequence, string Hash)?> ReadTailOrReportAsync(
        DbContext context, int timeoutSeconds, int pendingRows, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadTailAsync(context, timeoutSeconds, cancellationToken);
        }
        /*
          WHY THE MESSAGE BELOW SAYS "ON THIS ATTEMPT". ApplyAsync runs INSIDE
          Database.CreateExecutionStrategy().ExecuteAsync (see AzureBankDbContext.SaveChangesAsync),
          and production enables EnableRetryOnFailure. A transient SqlException on the tail read —
          1205, 233, 10053/10054/10060, 40197, 40613 — is logged HERE, then retried by the strategy,
          and the retry can succeed and answer 200. Worded as a completed refusal, the event an
          operator alerts on fired for money that moved.

          The tempting fix — log only once the strategy has given up — is WORSE. Carrying that fact
          upward means wrapping the exception in our own type, and a wrapped SqlException is no
          longer visible to SqlServerRetryingExecutionStrategy.ShouldRetryOn, so transient tail-read
          failures would stop being retried at all. Trading a false alarm for real lost retries is
          the wrong trade; the message tells the truth instead, and the runbook says to alert on
          repetition rather than on a single line.
        */
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            /*
              A CLIENT HANGING UP IS NOT AN AUDIT FAILURE. Without this branch, a browser that
              navigates away mid-request produces an Error-level AuditChainUnavailable naming a
              timeout that never happened — and since this event is what an operator alerts on, the
              alert would count disconnects alongside the thing it exists for. Rethrown unlogged:
              the movement still does not happen, because nobody is waiting for it.
            */
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "SecurityEvent {SecurityEvent}: the audit chain tail could not be read within "
                    + "{TimeoutSeconds}s, so {PendingRows} pending audit row(s) and the action they "
                    + "describe are refused ON THIS ATTEMPT. A transient fault may be retried by the "
                    + "execution strategy and then succeed, so a single line is a blip and repetition "
                    + "is an outage",
                SecurityEvents.AuditChainUnavailable,
                timeoutSeconds,
                pendingRows);
            throw;
        }
    }

    /// <summary>
    /// How long the tail read may wait for its lock before the money movement it belongs to is
    /// refused. Validated at startup as 1–300 seconds; see <see cref="AuditOptions"/>.
    /// </summary>
    /*
      THE BOUND COMES FROM THE OPTIONS THIS CHAIN WAS CONSTRUCTED WITH, and from nothing else.

      An earlier version walked the DbContext's internal service provider and then its application
      service provider looking for IOptions<AuditOptions>, on the assumption that the chain could not
      otherwise see the host's configuration. It can. AuditChain is registered AddScoped and receives
      the same IOptions the host binds — which is how ChainKey has always been read a few lines
      below. The walk was answering a question that was never open.

      Measured after deleting it: the SQL contention test sets the bound to one second through
      UseSetting, and the refusal still came back in 1,136 ms. The configured value arrives here
      either way.

      Deleting it also removed a trap worth recording. DbContext.GetService<T>() THROWS for an
      unregistered service rather than returning null, so that path needed the raw IServiceProvider
      and careful casts to stay safe in a manually constructed context. It was buying nothing.
    */
    private int TailTimeoutSeconds => _options.Value.TailTimeoutSeconds;

    private static (long Sequence, string Hash)? ReadTail(DbContext context, int timeoutSeconds)
    {
        if (context.Database.IsRelational())
        {
            var restore = context.Database.GetCommandTimeout();
            context.Database.SetCommandTimeout(timeoutSeconds);
            try
            {
                var row = context.Set<AuditEvent>()
                    .FromSqlRaw(TailSql)
                    .AsNoTracking()
                    .Select(e => new { e.Sequence, e.RowHash })
                    .FirstOrDefault();
                return row is null ? null : (row.Sequence, row.RowHash);
            }
            finally
            {
                context.Database.SetCommandTimeout(restore);
            }
        }

        var inMemory = NonRelationalTail(context).FirstOrDefault();
        return inMemory is null ? null : (inMemory.Sequence, inMemory.RowHash);
    }

    /*
      The InMemory provider has no locks and no IDENTITY, so Sequence stays 0 on every row and cannot
      order anything; Id — a UUIDv7 — can. Guarded on IsRelational() rather than IsInMemory() for the
      reason IdempotencyCleanupService gives for the same choice: it keeps the InMemory provider
      package out of the production API.

      Be precise about what the ~585 InMemory tests can therefore prove: that the chain LINKS, and
      that tampering breaks it. NOT that concurrent writers do not fork, because nothing there
      serialises. That property belongs to the SQL Server proofs, and claiming it from an InMemory
      test would be the "green and false" state this project refuses.
    */
    private static IQueryable<AuditEvent> NonRelationalTail(DbContext context) =>
        context.Set<AuditEvent>()
            .AsNoTracking()
            .OrderByDescending(e => e.Sequence);

    public async Task<AuditChainVerification> VerifyAsync(
        DbContext context, CancellationToken cancellationToken = default)
    {
        /*
          Read in Sequence order — the same order Link() wrote them in. Ordering by OccurredAt would
          be wrong twice over: two rows can share a millisecond, and a clock can move backwards.
          AsNoTracking because a verifier must never be able to write back what it read.
        */
        /*
          STREAMED, NOT BUFFERED, and the difference only shows on a table that is not a test fixture.
          This read the whole table with ToListAsync. Measured on SQL Server at 20,006 rows: 207 ms
          and 12 MB of managed heap — roughly 0.6 KB per row, and LINEAR. A bank's audit trail reaches
          millions of rows inside a year, which is ~600 MB at one million and gigabytes beyond, for a
          walk that never needs more than one row at a time.

          The fix is measured at two sizes rather than one, because "it dropped" does not establish
          the shape and the shape is the whole claim: 5,006 rows -> 2,671 KB, and 40,006 rows ->
          610 KB. Eight times the rows did not cost eight times the memory (linear would have been
          ~21 MB); the absolute figures are GC noise around a flat line. Time is sub-linear too,
          148 ms to 269 ms, since fixed cost dominates at these sizes.

          The cost of streaming is a data reader held open for the length of the walk. That is the
          right trade for a verifier run deliberately by an operator, and the wrong one for anything
          on the money path — which is why nothing on the money path calls this.

          IT ONLY STREAMS IF THE CONTEXT HAS NO RETRYING EXECUTION STRATEGY, and that condition is
          invisible from here. A stream cannot be replayed from the middle, so EF sets
          QueryCompilationContext.IsBuffering from ExecutionStrategy.RetriesOnFailure and PRE-BUFFERS
          the whole resultset — AsAsyncEnumerable() then streams in name only. Measured on 40,006
          rows: 3 MB with retry off against 34 MB with it on, the 34 MB present before the first row
          is examined.

          So the caller decides whether this line means anything. AzureBank.AuditVerifier passes
          retryOnTransientFailures: false to AddInfrastructure for exactly this reason, and a test
          pins it; every writer keeps retry and buffers, which is correct for the small saves they
          make. Changing that argument silently reverts this method to what it replaced.
        */
        var rows = context.Set<AuditEvent>()
            .AsNoTracking()
            .OrderBy(e => e.Sequence)
            .AsAsyncEnumerable();

        string? previous = null;
        long verified = 0;

        // Recorded as the walk goes, so the range and the count are two facts about ONE read.
        long? lowest = null;
        long? highest = null;

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            lowest ??= row.Sequence;
            highest = row.Sequence;

            if (row.PreviousHash != previous)
            {
                return new AuditChainVerification(
                    verified,
                    row.Sequence,
                    $"Row {row.Id} expected to follow '{previous ?? "(start of chain)"}' but records "
                    + $"'{row.PreviousHash ?? "(start of chain)"}'. A row was deleted, reordered, or inserted.",
                    lowest,
                    highest);
            }

            var expected = ComputeRowHash(row);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(row.RowHash), Encoding.ASCII.GetBytes(expected)))
            {
                return new AuditChainVerification(
                    verified,
                    row.Sequence,
                    $"Row {row.Id} does not match its own hash: it was altered after it was written.",
                    lowest,
                    highest);
            }

            previous = row.RowHash;
            verified++;
        }

        return new AuditChainVerification(verified, null, null, lowest, highest);
    }

    /// <summary>
    /// HMAC-SHA256 over a versioned, delimited rendering of the row AND the hash before it — which is
    /// what makes it a chain rather than a set of independent checksums.
    /// </summary>
    /// <remarks>
    /// The version prefix is the same device <c>StepUpAuthorizationService</c> uses: adding a field
    /// later must invalidate every previously computed value rather than silently leaving the new
    /// field unprotected. It reads <c>v2</c> because <see cref="AuditEvent.Sequence"/> was added to
    /// the payload after the first round of review — see the block below. <c>|</c> is a safe
    /// delimiter because every part is a Guid, an enum name, an integer or hex — except
    /// <c>Detail</c>, which is caller-supplied, and is therefore placed LAST where an embedded
    /// delimiter cannot shift the meaning of a field after it.
    /// </remarks>
    /*
      SEQUENCE IS HASHED, and it was not in v1. Sequence is the column VerifyAsync orders by, so
      leaving it outside the payload meant the one field that defines the chain's order was the one
      field the chain did not protect. Be precise about what that did and did not allow, because the
      review that raised it overstated the consequence: REORDERING an interior row is already caught
      without this, since the PreviousHash links stop lining up. What was genuinely unprotected was
      the tail — renumbering the last row to an unused higher value changed nothing verifiable.

      No exploit was constructed from that, and none is claimed here. It is hashed because it costs
      one field and removes the question entirely, which is a better resting place than an argument
      about how narrow the hole is.
    */
    /*
      OccurredAt IS HASHED AS TICKS AND NOT AS ISO-8601, and this is a correction, not a preference.
      The first version used ToString("O"), which renders a trailing "Z" for a DateTime whose Kind is
      Utc and no "Z" for one whose Kind is Unspecified. SQL Server's datetime2 stores no kind, so a
      row written from DateTime.UtcNow hashed WITH the Z and, read back, hashed WITHOUT it: every
      audit row would have failed verification the moment it came from the database — which is to
      say always, in production. It was invisible on the InMemory provider, where the identity map
      hands back the very object that was written.

      MEASURED before the fix, on LocalDB, recomputing a stored row's HMAC outside .NET:
        payload with    "2026-08-19T10:39:24.7403300Z" -> 6a7028...143903  == the stored RowHash
        payload with    "2026-08-19T10:39:24.7403300"  -> a98d42...c3dbd9  != the stored RowHash
      Ticks is a plain integer and round-trips exactly through datetime2(7): same 100-ns resolution,
      nothing kind-dependent to lose. AuditEvent.OccurredAt is UTC by convention, stated there.
    */
    private string ComputeRowHash(AuditEvent row)
    {
        var payload = string.Join('|',
            "v2",
            row.Id.ToString("N"),
            row.Sequence.ToString(CultureInfo.InvariantCulture),
            row.OccurredAt.Ticks.ToString(CultureInfo.InvariantCulture),
            row.Event,
            row.Outcome.ToString(),
            row.ActorUserId?.ToString("N") ?? string.Empty,
            row.SubjectType ?? string.Empty,
            row.SubjectId?.ToString("N") ?? string.Empty,
            row.TraceId ?? string.Empty,
            row.PreviousHash ?? string.Empty,
            row.Detail ?? string.Empty);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.Value.ChainKey));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}
