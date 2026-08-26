using System.Data.Common;
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

/// <summary>What kind of break the walk found, when it found one.</summary>
/// <remarks>
/// They are not interchangeable and an operator acts differently on each, which is why the
/// verdict carries the kind instead of leaving callers to match on the wording of a sentence.
/// <para>
/// A WRONG KEY USED TO PRODUCE ONLY <see cref="HashMismatch"/>, AND THAT IS NOW A v2-ONLY
/// STATEMENT. It still holds for a row that records no key identity: a wrong key cannot make such a
/// row unreadable and cannot change what it records as its predecessor, so the hash is the only
/// thing left to break. A row that DOES name its key is checked against that name before its hash is
/// recomputed, so a wrong key produces <see cref="UnknownScheme"/> there and never reaches the hash.
/// The practical consequence is the useful one: on such a row a hash mismatch is no longer
/// ambiguous, because the key behind it has already been confirmed.
/// </para>
/// </remarks>
public enum AuditChainBreakKind
{
    /// <summary>No break: the walk reached the end.</summary>
    None = 0,

    /// <summary>A row does not hash to what is stored beside it. A wrong key looks like this too.</summary>
    HashMismatch,

    /// <summary>A row records a predecessor that is not the row before it. Deleted, reordered or inserted.</summary>
    LinkBroken,

    /// <summary>A row could not be materialised at all, so a stored value contradicts the schema.</summary>
    Unreadable,

    /// <summary>
    /// The row names a payload version or a key identity this verifier cannot apply, so its hash was
    /// never recomputed.
    /// </summary>
    /// <remarks>
    /// THIS IS A BREAK, NOT A NOTE, AND THE DISTINCTION IS THE WHOLE REASON IT EXISTS. It is
    /// tempting to treat "I cannot check this row" as a configuration remark and let the walk
    /// continue: that would hand an attacker a muzzle. Overwrite a tampered row's key identity with
    /// something no key produces and the verdict would soften from tampering to housekeeping,
    /// which is the opposite of what the column is for. Both values live inside the row's own hashed
    /// payload, so a stored value that is not what the schema says it must be is itself a
    /// modification — the same reasoning <see cref="Unreadable"/> already carries.
    /// <para>
    /// It says nothing about WHY. A verifier holding a different key, an older build that cannot
    /// render this version, and an overwritten column all print this, and the discriminator is
    /// positional rather than textual: the first two fail at the lowest-sequence row of that scheme
    /// and at every one after it, while a single interior row failing among verified siblings is a
    /// write.
    /// </para>
    /// </remarks>
    UnknownScheme,
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
/// <param name="Kind">Which break it was, when there was one.</param>
/// <param name="PayloadVersion">
/// The version the breaking row declared, when the break was an <see cref="AuditChainBreakKind.UnknownScheme"/>.
/// </param>
/// <param name="RecordedKeyId">The key identity that row carried, or null if it carried none.</param>
/// <param name="ConfiguredKeyId">The identity of the key THIS verification holds.</param>
/// <remarks>
/// THE RANGE COMES FROM THE WALK ITSELF, and it is here rather than left to the caller because the
/// caller cannot get it right. Asking the database separately for MIN and MAX is two more statements
/// at two more instants: a row committed between the MAX and the walk is counted but falls outside
/// the range, so the tool could report 101 rows verified over a range ending at 100. The count and
/// the range exist to be compared with each other, so they have to come from one read.
/// </remarks>
/// <remarks>
/// The last three carry what a <see cref="AuditChainBreakKind.UnknownScheme"/> verdict could not
/// apply, for the same reason <see cref="Kind"/> exists at all: a caller that has to parse them back
/// out of <see cref="Reason"/> is matching on the wording of a sentence. They are optional so that
/// every construction that predates them still compiles and still means what it did.
/// </remarks>
public readonly record struct AuditChainVerification(
    long Verified,
    long? FirstBrokenSequence,
    string? Reason,
    long? LowestSequence = null,
    long? HighestSequence = null,
    AuditChainBreakKind Kind = AuditChainBreakKind.None,
    string? PayloadVersion = null,
    string? RecordedKeyId = null,
    string? ConfiguredKeyId = null)
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

    /// <summary>The payload rendering new rows are written with.</summary>
    internal const string CurrentPayloadVersion = "v3";

    /// <summary>The rendering rows were written with before key identity was recorded.</summary>
    internal const string LegacyPayloadVersion = "v2";

    /*
      THE DOMAIN STRING AND THE TRUNCATION LENGTH ARE ONE DECISION WITH THE COLUMN WIDTH, and all
      three are frozen together the moment the first v3 row is written: the identity is inside that
      row's hashed payload, so changing any of them changes stored hashes that cannot be recomputed.
      Its own "v1" is the identity scheme's version and has nothing to do with the payload version --
      they are separate ladders and bumping one must never bump the other.

      Sixteen hex characters is 64 bits, which is a key IDENTIFIER rather than a secret: its job is
      to tell one key from another and to let a verifier confirm the key it holds, not to resist
      anything. Publishing it is not a widening -- a database-only attacker already has an offline
      oracle for guessing the key in every row's RowHash.
    */
    private const string KeyIdDomain = "AzureBank.Audit.KeyId.v1";
    private const int KeyIdHexLength = 16;

    private readonly string _keyId;

    public AuditChain(IOptions<AuditOptions> options, ILogger<AuditChain> logger)
    {
        _options = options;
        _logger = logger;

        // Once, here, and never inside the walk or inside the UPDLOCK/HOLDLOCK window that every
        // business write waits behind.
        _keyId = DeriveKeyId(options.Value.ChainKey);
    }

    /// <summary>
    /// The non-secret identity of a chain key: HMAC-SHA256 over a fixed domain string, keyed by the
    /// key itself, truncated to <see cref="KeyIdHexLength"/> lowercase hex characters.
    /// </summary>
    /// <remarks>
    /// DERIVED RATHER THAN CONFIGURED so that it cannot be wrong. A configured identifier is a second
    /// place to state the same fact, and the two drift with nothing detecting it; a derived one lets a
    /// verifier recompute the identity from the key it actually holds and say plainly when that key is
    /// not the one that wrote the row. This repository configures a key id elsewhere, for the password
    /// hasher, and the difference is the drain path: a credential is re-hashed on next use, so a
    /// drifted id there corrects itself. An audit row is evidence and is never rewritten, so nothing
    /// would ever correct it.
    /// </remarks>
    internal static string DeriveKeyId(string chainKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(chainKey));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(KeyIdDomain)))[..KeyIdHexLength];
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
    /// <summary>True when the failure is about the STORE rather than about a stored value.</summary>
    private static bool IsInfrastructureFailure(Exception failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is DbException)
            {
                return true;
            }
        }

        return false;
    }

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

            /*
              ASSIGNED UNCONDITIONALLY, exactly like RowHash below it and for the same reason. These
              two columns are the verifier's instruction for how to read the row, so they come from
              the component that renders the payload, never from the one that supplied the content.
              Honouring a value a caller had already set would let a row ship declaring a scheme it
              was not written under -- and the promise that the column and the prefix cannot disagree
              is empty unless exactly one authority writes the string.
            */
            row.PayloadVersion = CurrentPayloadVersion;
            row.KeyId = _keyId;

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

        /*
          A ROW THAT CANNOT EVEN BE READ IS A CHAIN PROBLEM, NOT A CONNECTION PROBLEM.

          Outcome is stored as a string and mapped to an enum, so a single UPDATE writing a value
          that is not an enum member makes EF throw while MATERIALISING that row -- mid-stream,
          after the walk has begun. Left to propagate, the verifier's outermost catch classified it
          as "no verdict ... this is NOT a statement about the chain", which is exactly wrong: the
          Outcome column is inside the hashed payload, so an unreadable value there IS a
          modification of a row.

          Demonstrated as an attack on the verifier itself: tamper with row 5's Detail, then write
          one bogus Outcome on row 1, and the tool stops reporting BROKEN and reports a
          configuration problem instead. One statement muzzles the thing whose whole purpose is to
          notice. So the enumeration is guarded here, where the walk knows how far it got, and the
          failure is reported as what it is.
        */
        /*
          await using, BECAUSE THE HAND-DRIVEN LOOP LOST WHAT await foreach GAVE FOR FREE.

          This method used to be an await foreach, which the compiler lowers to a try/finally that
          disposes the enumerator on every exit path. Driving the enumerator by hand -- needed so a
          row that will not materialise can be caught and reported rather than escaping -- dropped
          that finally, so every EARLY return, which is to say every BROKEN verdict, left EF's
          DbDataReader open on this context's connection.

          Measured on SQL Server: the caller's next query on the same context died with "There is
          already an open DataReader associated with this Connection which must be closed first."
          The InMemory tests could not see it -- there is no reader there -- which is why it took a
          reviewer reading the rewrite, and why the regression test for it is SQL-gated.
        */
        await using var enumerator = rows.WithCancellation(cancellationToken).GetAsyncEnumerator();

        while (true)
        {
            AuditEvent row;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                row = enumerator.Current;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception unreadable) when (!IsInfrastructureFailure(unreadable))
            {
                /*
                  ONLY A DATA FAILURE LANDS HERE. The first version of this guard caught everything
                  the enumeration could throw, which turned an unreachable database into CHAIN
                  BROKEN -- the exact collision this tool spent three rounds separating. The suite's
                  own unreachable-database test caught it, going from 3 to 1.

                  A DbException anywhere in the chain means the STORE failed and there is no verdict
                  to give, so it propagates. Anything else -- an enum value that is not a member, a
                  column that will not materialise -- is a stored value contradicting the schema,
                  which is a modification of a row.
                */
                /*
                  FirstBrokenSequence MUST BE NON-NULL HERE, because IsIntact is defined as its
                  absence. The first version of this guard returned null when nothing had been read
                  yet -- which is precisely the case where the FIRST row is the poisoned one -- so
                  the verdict came back "intact with zero rows", i.e. NOTHING TO VERIFY, exit 2.
                  That moved the muzzle rather than removing it. Measured before and after.

                  When the walk got nowhere the position is genuinely unknown, so it is reported as
                  the row after the last one read, falling back to the first. The NUMBER is
                  best-effort; the reason text below carries what is actually known.
                */
                return new AuditChainVerification(
                    verified,
                    (highest ?? 0) + 1,
                    $"The row after sequence {highest?.ToString() ?? "(start of chain)"} could not be "
                    + $"read at all: {unreadable.Message} A stored value is not what the schema says "
                    + "it must be, which is itself a modification -- the column it failed on is "
                    + "inside the hashed payload.",
                    lowest,
                    highest,
                    AuditChainBreakKind.Unreadable);
            }

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
                    highest,
                    AuditChainBreakKind.LinkBroken);
            }

            /*
              SCHEME, AND IT SITS HERE ON PURPOSE -- AFTER THE LINK, BEFORE THE HASH.

              After the link, because the link check needs neither key nor version: it is what makes
              a decapitated chain report LinkBroken instead of reaching a hash it cannot recompute. A
              scheme problem must never be able to mask a deleted prefix.

              Before the hash, because recomputing a row under a scheme it was not written with
              produces a mismatch that reads exactly like tampering -- which is the defect this
              column exists to remove. A row this verifier cannot render is reported as unchecked,
              never as wrong.

              It is a BREAK either way. Letting the walk continue past a row it could not check would
              let "intact" mean "intact except for the rows I skipped", and a verdict that quietly
              excludes its own failures is the green-and-false state this project treats as the worst
              one.
            */
            if (row.PayloadVersion is not (CurrentPayloadVersion or LegacyPayloadVersion))
            {
                return new AuditChainVerification(
                    verified,
                    row.Sequence,
                    $"Row {row.Id} declares payload version '{row.PayloadVersion}', which this build "
                    + "cannot render, so its hash was NOT checked. Either it was written by a newer "
                    + "build, or the column was overwritten -- and that column is inside the hashed "
                    + "payload. This is not a configuration note.",
                    lowest,
                    highest,
                    AuditChainBreakKind.UnknownScheme,
                    row.PayloadVersion,
                    row.KeyId,
                    _keyId);
            }

            // A v2 payload has no key-identity element, so an id sitting on such a row is unhashed
            // and unexplained. Without this the NULL rule is a hole the width of the column.
            if (row.PayloadVersion == LegacyPayloadVersion && row.KeyId is not null)
            {
                return new AuditChainVerification(
                    verified,
                    row.Sequence,
                    $"Row {row.Id} is a '{LegacyPayloadVersion}' row carrying key id "
                    + $"'{row.KeyId}'. That version records no key identity, so this value is "
                    + "outside the hashed payload and nothing wrote it legitimately. Its hash was "
                    + "NOT checked.",
                    lowest,
                    highest,
                    AuditChainBreakKind.UnknownScheme,
                    row.PayloadVersion,
                    row.KeyId,
                    _keyId);
            }

            if (row.PayloadVersion == CurrentPayloadVersion && row.KeyId != _keyId)
            {
                return new AuditChainVerification(
                    verified,
                    row.Sequence,
                    $"Row {row.Id} was written under key id '{row.KeyId ?? "(none)"}' and this "
                    + $"verification holds the key whose id is '{_keyId}', so its hash was NOT "
                    + "checked. Either this is the wrong key for this row, or the column was "
                    + "overwritten. Which one is positional: a wrong key fails at the LOWEST "
                    + $"'{CurrentPayloadVersion}' row and every one after it, while a single row "
                    + "failing among verified siblings is a write.",
                    lowest,
                    highest,
                    AuditChainBreakKind.UnknownScheme,
                    row.PayloadVersion,
                    row.KeyId,
                    _keyId);
            }

            var expected = ComputeRowHash(row);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(row.RowHash), Encoding.ASCII.GetBytes(expected)))
            {
                return new AuditChainVerification(
                    verified,
                    row.Sequence,
                    $"Row {row.Id} does not match its own hash. Either it was altered after it was "
                    + "written, or this verification is using a different Audit:ChainKey from the "
                    + "one it was written with.",
                    lowest,
                    highest,
                    AuditChainBreakKind.HashMismatch);
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
    /*
      DISPATCHED ON THE ROW'S OWN VERSION, which is the point of the column. Before this, the version
      was a literal compiled in here, so bumping it re-rendered every historical row under the new
      scheme and reported honest ones as tampering -- the failure this whole change removes. A
      renderer added here is added forever: rows written under it can never be re-hashed once an
      anchor certifies them, so an old arm is deleted only if no row anywhere still declares it.

      Element ONE is row.PayloadVersion rather than the literal, in BOTH arms. That is what makes the
      column and the prefix the same thing rather than two copies of it: overwrite the column and the
      hash stops matching, so the declaration is protected by the very hash it selects.
    */
    private string ComputeRowHash(AuditEvent row) => row.PayloadVersion switch
    {
        CurrentPayloadVersion => Hmac(string.Join('|',
            row.PayloadVersion,
            row.KeyId ?? string.Empty,
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
            row.Detail ?? string.Empty)),

        // Byte-identical to what shipped, with the sole change that element one reads the column
        // instead of a literal -- so historical rows keep the hashes they were written with.
        LegacyPayloadVersion => Hmac(string.Join('|',
            row.PayloadVersion,
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
            row.Detail ?? string.Empty)),

        // Unreachable through the walk, which refuses an unknown version before it gets here. It
        // throws rather than returning a hash nothing can match, because a caller that reaches this
        // has skipped the check that exists to stop it.
        _ => throw new InvalidOperationException(
            $"No payload renderer for version '{row.PayloadVersion}'. The walk checks this before "
            + "recomputing a hash; reaching here means that check was bypassed."),
    };

    private string Hmac(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.Value.ChainKey));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}
