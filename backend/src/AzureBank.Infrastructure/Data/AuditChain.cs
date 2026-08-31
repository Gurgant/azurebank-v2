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
    /// It says nothing about WHY, and the walk reaches it by NINE paths: this build cannot render
    /// the version the row declares; a <c>v2</c> row carries a key id, which that version has
    /// nowhere to keep; a <c>v3</c> row carries none, which its version does keep; no key in the
    /// ring has the row's id; four boundary paths — the row sits ABOVE or BELOW the epoch of the key
    /// that answers for it, once for a row that NAMES that key and once for a row that records no
    /// identity and is answered for by <c>Audit:FoundingChainKey</c>; and one that is not a boundary
    /// at all — an identity-less row stored BELOW sequence 1, which reaches the same comparison
    /// because the founding epoch starts at 1 on a ring that has never rotated.
    /// <para>
    /// The verifier prints SEVEN causes rather than nine, because two PAIRS of paths take one action
    /// each. The identity-column pair — a <c>v2</c> row carrying an id and a <c>v3</c> row carrying
    /// none — says the column contradicts the version, so the value was written after the fact. The
    /// below-the-founding-epoch pair prints different TEXT, one sending the operator to the
    /// designation and the other to escalation, but occupies one entry in the printed list, which
    /// names both. Every other path takes a different action, which is why they are separate.
    /// </para>
    /// <para>
    /// ⚠️ THIS PARAGRAPH ENUMERATED EIGHT ITEMS UNDER A HEADING THAT SAID NINE, and closed with
    /// "rather than eight". The ninth path was added in the same commit that changed the heading and
    /// nowhere else. The count is DERIVED from this file by
    /// <c>AuditVerifierReportTests.TheUnknownSchemeBlockEnumeratesEVERYWayToReachIt…</c>, which
    /// reddens when a path is added; the ENUMERATION is not, and this is what that costs.
    /// </para>
    /// <para>
    /// An overwritten column is not a TENTH path: it is how several of those come about, because
    /// both columns are inside the hashed payload.
    /// <para>
    /// The nine paths fall into THREE shapes, and how wide the damage is depends on which:
    /// </para>
    /// <list type="bullet">
    /// <item>ROW-LOCAL — an unrenderable payload version, and the identity column contradicting the
    /// version. These concern no epoch and no key: the row is refused on its own, and the rows
    /// around it are untouched.</item>
    /// <item>A WHOLE EPOCH — a key the ring does not hold. The rows it answers for ARE its stretch,
    /// so the walk stops at the first of them.</item>
    /// <item>OUTSIDE an epoch — the four boundary paths, plus the below-sequence-1 one. The failing
    /// row is by definition NOT in the epoch the verdict names, so that epoch says where the key was
    /// valid, never where the damage is.</item>
    /// </list>
    /// <para>
    /// ⚠️ THIS SAID "each path applies to an INTERVAL — the epoch of the key it concerns", which is
    /// true of ONE of the three shapes. Raised in review on the runbook's copy of the same sentence;
    /// the verifier's printed copy had already been narrowed to five of the seven causes and still
    /// said nothing about the two row-local ones.
    /// </para>
    /// <para>
    /// ⚠️ SO THE POSITIONAL DISCRIMINATOR IS GONE, AND THIS PARAGRAPH SOLD IT TWICE. It said each
    /// path "fails at the lowest-sequence row it applies to and at every one after it, while a
    /// single interior row failing among verified siblings is a write" — true when one key answered
    /// for every <c>v3</c> row, false with a ring: a key missing from <c>Audit:RetiredChainKeys</c>
    /// fails over its own epoch and nothing above it, so it breaks in the middle with verified rows
    /// beneath, which is exactly the shape attributed to a write. It then said the below-the-epoch
    /// paths apply to a PREFIX, "every row from the bottom of the table up to the previous key's
    /// boundary" — they apply to the rows naming that key, not to everything beneath. What separates
    /// a configuration miss from a write is a second run after adding the named id to the ring.
    /// </para>
    /// </para>
    /// <para>
    /// This paragraph said "three" and led with "a verifier holding a different key", which the
    /// boundary verdicts make false — the ring HOLDS the key in all four. It then said "six" while
    /// the walk had eight, because the count was corrected in the verifier's output and in the
    /// runbook and this copy was missed AGAIN, one commit after the paragraph below it says that is
    /// the shape of every stale claim on this branch. Counted from the returns now, and the returns
    /// are listed above so the next person can count them too.
    /// </para>
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
/// <param name="TailRowHash">
/// The <c>RowHash</c> of the last row this walk verified, and null on every break.
/// </param>
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
    string? ConfiguredKeyId = null,
    string? TailRowHash = null)
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
    /// <summary>
    /// The strength floor every key in the ring is held to, mirroring the value both composition
    /// roots apply to <c>Audit:ChainKey</c>. Stated once here because the ring is the only place
    /// that sees retired keys at all.
    /// </summary>
    internal const int MinimumKeyLength = 32;

    private const string KeyIdDomain = "AzureBank.Audit.KeyId.v1";
    private const int KeyIdHexLength = 16;

    private readonly string _keyId;

    /// <summary>
    /// Key id to (material, FIRST sequence it answers for, LAST sequence it answers for), for
    /// VERIFICATION only — writing always uses the current key. Every entry is bounded BELOW,
    /// including the current key, whose epoch starts one past the last retirement; only the current
    /// key is unbounded above, because it answers for whatever it writes next. The bounds stop a row
    /// signed outside a key's epoch from being ACCEPTED — nothing stops it being written, which is a
    /// different sentence and the one that is true.
    /// <para>
    /// This said "(material, highest sequence it may answer for)" and "the current key is
    /// unbounded". The value has been a three-tuple since the epoch gained a lower end, and that
    /// same change bounded the current key below.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, (string Key, long FirstSequence, long? LastSequence)> _keyRing;

    /// <summary>
    /// The key that wrote the rows recording no key identity. Never assumed; see below.
    /// </summary>
    private readonly string _foundingKey;

    /// <summary>
    /// The sequence the FOUNDING key stopped writing at, or null while it is still the current key.
    /// </summary>
    /// <remarks>
    /// ⚠️ WITHOUT THIS, THE BOUNDARY ON EVERY OTHER KEY IS OPTIONAL — a forger picks the payload
    /// version. A 'v2' row records no key identity, so it is checked under the founding key without
    /// naming it; bounding retired keys by <c>KeyId</c> alone therefore left a route to the founding
    /// key that skipped the bound entirely. MEASURED before this field existed: a row minted with the
    /// retired founding key, at the tail, above its boundary, labelled 'v2' — verified clean,
    /// <c>IsIntact = True</c>. Before the ring the same row needed the CURRENT key, so this was the
    /// ring handing an old key a power it did not have, which is what the bound exists to prevent.
    /// </remarks>
    private readonly long? _foundingLastSequence;

    /// <summary>
    /// The sequence the FOUNDING key started at. 1 whenever the designation is what it should be —
    /// the oldest key in the ring — and higher only when the configuration says something it cannot
    /// mean, which the walk then refuses instead of quietly accepting.
    /// </summary>
    private readonly long _foundingFirstSequence;

    public AuditChain(IOptions<AuditOptions> options, ILogger<AuditChain> logger)
    {
        _options = options;
        _logger = logger;

        // Once, here, and never inside the walk or inside the UPDLOCK/HOLDLOCK window that every
        // business write waits behind.
        _keyId = DeriveKeyId(options.Value.ChainKey);

        /*
          THE RING IS BUILT HERE AND VALIDATED HERE, rather than in an options Validate() at startup,
          because there are two composition roots -- the API and the operator verifier -- and a
          structural rule enforced in one of them is a rule the other does not have. A constructor
          throw covers both, and the verifier already learned this lesson the other way round: a
          guard added to only one place is a guard with a hole the shape of the other place.

          IDS ARE DERIVED, never configured, for the reason DeriveKeyId records: a configured id is a
          second place to state the same fact and the two drift with nothing detecting it. That
          applies with more force to a retired key, whose rows can never be rewritten to correct a
          drifted id.
        */
        /*
          ⚠️ AN EPOCH HAS TWO ENDS. THE FIRST VERSION OF THIS RING GAVE IT NONE, THE SECOND GAVE
          IT ONE. Every boundary
          check was `row.Sequence > last` -- an upper bound only -- so a retired key answered for
          EVERY sequence from the bottom of the table up to its own retirement, including the
          stretches earlier keys wrote.

          MEASURED before this: with A retired at 2 and B retired at 4, the holder of B re-authored
          sequences 1 through 4 -- relabelling A's two rows as B's -- and the walk returned
          IsIntact = True. So compromising the NEWEST retired key handed over the whole history, not
          that key's own epoch, and each further rotation made the prize larger rather than smaller.
          That inverts the reason to rotate for the second time on this branch.

          THE LOWER BOUND IS DERIVED, NEVER CONFIGURED. The recorded boundaries already partition the
          sequence space: a key that stopped at N was preceded by one that stopped at N', so its
          epoch is (N', N]. Asking an operator for the start as well would be a second place to state
          one fact -- the same objection DeriveKeyId records against configured ids -- and the two
          would drift with nothing detecting it.
        */
        /*
          THE CONFIGURATION INDEX IS CARRIED THROUGH THE SORT, because the messages below name it and
          a sorted position is not the position an operator edits. Ordering by boundary is what makes
          the epochs derivable; reporting by that order would send somebody to `:1` for a mistake
          they made in `:0`.
        */
        /*
          THE FLOOR APPLIES TO THE CURRENT KEY TOO, and it did not. Both composition roots hold
          Audit:ChainKey to the same length, so this looked covered -- but that is precisely the
          argument the block above rejects for everything else in the ring: "a structural rule
          enforced in one of them is a rule the other does not have." The current key was the one
          member governed only by the roots, so a caller constructing AuditChain directly got no
          check at all. Measured: ChainKey = "" built a ring.
        */
        if (string.IsNullOrWhiteSpace(options.Value.ChainKey)
            || options.Value.ChainKey.Length < MinimumKeyLength)
        {
            /*
              TWO REASONS REACH THIS AND THE MESSAGE HAS TO SAY WHICH. The guard is blank OR short,
              and a key of forty spaces is blank while satisfying the only remedy a length-only
              message gives -- "Audit:ChainKey is 40 characters. It must be at least 32" tells the
              operator to lengthen something already long enough.
            */
            throw new AuditKeyRingException(
                (string.IsNullOrWhiteSpace(options.Value.ChainKey)
                    ? "Audit:ChainKey is blank. "
                    : $"Audit:ChainKey is {options.Value.ChainKey.Length} characters. ")
                + $"It must be non-blank and at least {MinimumKeyLength} characters, the same floor "
                + "every retired key is held to: it authenticates every row written from here on, "
                + "and a key weak enough to guess makes them forgeable by anyone holding the "
                + "database.");
        }

        var retiredEntries = (options.Value.RetiredChainKeys ?? [])
            .Select((entry, index) => (Entry: entry, Index: index))
            .OrderBy(e => e.Entry?.LastSequence ?? long.MinValue)
            .ToList();

        /*
          A NULL ENTRY IS REFUSED, NOT DROPPED, and it used to be dropped by a Where() one line up.
          Silently is the problem: a dropped entry left the ring with one key, so the deployment read
          as "never rotated" and the Audit:FoundingChainKey requirement never fired. A configuration
          that says a rotation happened would have produced a verifier that believed none had.

          ⚠️ THE SHAPE THAT REACHES THIS IS A CALLER PASSING null, NOT A JSON null. This comment said
          the entry came from `"RetiredChainKeys": [ null ]`; measured with the real binder, that
          element binds to a NON-null RetiredChainKey whose Key is empty and whose LastSequence is 0,
          so it was already refused by the blank-key guard above. What was dropped silently is a null
          the caller supplies directly — which every test in this file does, and which is one of the
          two ways this type is constructed.
        */
        foreach (var (entry, configIndex) in retiredEntries)
        {
            if (entry is null)
            {
                throw new AuditKeyRingException(
                    $"Audit:RetiredChainKeys:{configIndex} is null. An entry that binds to nothing "
                    + "cannot describe a rotation, and dropping it would leave the ring claiming "
                    + "fewer rotations than the configuration states.");
            }
        }

        _keyRing = new Dictionary<string, (string Key, long FirstSequence, long? LastSequence)>(
            StringComparer.Ordinal)
        {
            /*
              The current key answers from ONE PAST the last retirement and has no upper bound: it
              is the key in use, so it answers for whatever it writes next. Its FIRST sequence is not
              open either -- a row AT OR BELOW the last retirement was written by a key that had
              already stopped, so naming the current key there is as wrong as naming a retired key
              above its own boundary, and for the same reason.

              (This said "from the last retirement onwards", which is off by one in the permissive
              direction: the entry is built with LastSequence + 1, so a row at exactly the retirement
              sequence naming the current key is refused, and a reader trusting the sentence would
              have called that a bug.)
            */
            [_keyId] = (
                options.Value.ChainKey,
                retiredEntries.Count == 0 ? 1 : retiredEntries[^1].Entry!.LastSequence + 1,
                null),
        };

        // Walks in boundary order, so each entry's epoch starts where the previous one ended.
        long nextEpochStart = 1;

        foreach (var (entry, configIndex) in retiredEntries)
        {
            var retired = entry!.Key ?? string.Empty;
            if (string.IsNullOrWhiteSpace(retired))
            {
                throw new AuditKeyRingException(
                    $"Audit:RetiredChainKeys:{configIndex} is blank. A blank key cannot have written "
                    + "any row, so its presence is a configuration mistake rather than a no-op.");
            }

            /*
              THE SAME STRENGTH FLOOR THE CURRENT KEY HAS, and it was missing. Both composition roots
              hold Audit:ChainKey to 32 characters -- see each root's ServiceCollectionExtensions --
              while a retired key was checked only for being
              non-blank. A three-character retired key would have been accepted and would then have
              been the only thing standing behind every row in its epoch, which is the stretch of the
              trail nobody can rewrite to repair. A key weak enough to guess makes its epoch forgeable
              by anyone holding the database, which is the attacker the chain is built for.

              The floor lives here rather than in each root's options validation for the reason the
              block above gives: a structural rule enforced in one root is a rule the other lacks.
            */
            if (retired.Length < MinimumKeyLength)
            {
                throw new AuditKeyRingException(
                    $"Audit:RetiredChainKeys:{configIndex} holds a key of {retired.Length} "
                    + "characters. A retired "
                    + $"key must be at least {MinimumKeyLength}, the same floor Audit:ChainKey is "
                    + "held to in both composition roots: it authenticates every row in its epoch, "
                    + "and those rows can never be rewritten under a stronger key.");
            }

            // A retired key with no boundary is the unbounded ring this bound exists to prevent, so
            // it is refused rather than defaulted -- a default here would be silently permissive,
            // and the permissive direction is the one that hands an old key a new power.
            if (entry!.LastSequence < 1)
            {
                throw new AuditKeyRingException(
                    $"Audit:RetiredChainKeys:{configIndex} has LastSequence {entry.LastSequence}. A "
                    + "retired key answers only for rows at or below the sequence it stopped writing "
                    + "at; without that boundary it could authenticate rows minted after it was "
                    + "retired, which is the regression the boundary exists to prevent.");
            }

            /*
              TWO RETIRED KEYS CLAIMING THE SAME BOUNDARY CANNOT BOTH BE ANSWERED FOR, because the
              rows beneath it would belong to whichever happened to sort first -- and sort order
              between equal keys is not something a configuration file states. It is refused rather
              than resolved, since resolving it means guessing which key wrote history.

              ⚠️ AND A KEY THAT WROTE NOTHING IS THE SAME CONFIGURATION, not a different one. This
              paragraph used to say a zero-write rotation "gives the second key an EMPTY epoch …
              and that key correctly answers for no rows", one line above the guard that makes it
              impossible. Measured: 512 boundary triples, ZERO produce an empty epoch — entries are
              sorted ascending, so only equality is reachable, and equality is what this refuses.

              Refusing is right, and not merely what the code happens to do. A key that wrote nothing
              has no row naming its id, so a ring entry for it answers for nothing; its only effect
              is to make the boundary beneath it ambiguous. The honest configuration for a rotation
              with no writes is to leave that key OUT of the ring entirely.
            */
            if (entry.LastSequence == nextEpochStart - 1 && nextEpochStart > 1)
            {
                /*
                  ⚠️ AND THIS GUARD RUNS BEFORE THE DUPLICATE-ID ONE, so it sees a wholly duplicated
                  entry first -- the same key material pasted into slot 1 with the same boundary --
                  and used to call that "two keys ending at the same row". It is one key listed
                  twice, and the operator who edits a boundary on that advice is changing a number
                  every other message on this branch tells them to take only from the rotation
                  record. The message says which it is, from the material, rather than assuming.
                */
                var duplicateOfPrevious = _keyRing.ContainsKey(DeriveKeyId(retired));

                throw new AuditKeyRingException(
                    $"Audit:RetiredChainKeys:{configIndex} ends at {entry.LastSequence}, the same "
                    + "sequence as the entry before it. "
                    + (duplicateOfPrevious
                        ? "It also holds a key the ring already has, so this is ONE key listed "
                          + "twice rather than two keys colliding: remove the duplicate entry. "
                          + "Editing the boundary would change a number that must come from the "
                          + "rotation record."
                        : "Boundaries partition the sequence space, so two keys ending at the same "
                          + "row leaves the rows beneath claimed by both and the ring cannot tell "
                          + "which one wrote them."));
            }

            /*
              ⚠️ THE EPOCH ARITHMETIC IS UNCHECKED, AND long.MaxValue MAKES IT WRAP THE WRONG WAY.
              Every epoch start is `previous LastSequence + 1`, including the CURRENT key's. C# is
              unchecked by default, so a boundary of long.MaxValue produces long.MinValue silently --
              and the current key would then answer for every row in the table, which is the one key
              a bound is supposed to be able to constrain from below.

              Refused rather than computed with `checked`, because an OverflowException escapes the
              AuditKeyRingException family and would leave `anchor` and `export` exiting 4 again.
              Refused rather than bounded to the table's tail, because a boundary above the tail is
              legitimate -- the runbook's triage table has a row for exactly that.
            */
            if (entry.LastSequence == long.MaxValue)
            {
                throw new AuditKeyRingException(
                    $"Audit:RetiredChainKeys:{configIndex} has LastSequence {entry.LastSequence}, "
                    + "the largest a sequence can be. Every epoch begins one past the previous "
                    + "boundary, so there is no room for an epoch above this one and the arithmetic "
                    + "that derives it would wrap.");
            }

            var id = DeriveKeyId(retired);

            // A retired key equal to the current one is not harmless: it reads as "we rotated" while
            // the ring holds one key, so the deployment believes history is covered when nothing
            // changed. Refused loudly rather than deduplicated quietly.
            if (!_keyRing.TryAdd(id, (retired, nextEpochStart, entry.LastSequence)))
            {
                throw new AuditKeyRingException(
                    $"Audit:RetiredChainKeys:{configIndex} holds a key whose id '{id}' is already "
                    + "in the ring — "
                    + (id == _keyId
                        ? "it is the CURRENT Audit:ChainKey. Retiring the key still in use would let "
                          + "the deployment believe it had rotated when it has not."
                        : "the same retired key is listed twice."));
            }

            nextEpochStart = entry.LastSequence + 1;
        }

        /*
          THE FOUNDING KEY IS NAMED, NEVER ASSUMED, and ADR-0044 chose that word before this code
          existed: "whatever adds a second key must add a ring entry for the FOUNDING key rather than
          silently re-point history at whatever is current." A null KeyId means no identity was
          recorded, not that the current key wrote it — so defaulting to the current key would
          re-attribute history at the moment of rotation and then report those rows as tampered.

          Empty is allowed ONLY while nothing has been retired, because then the current key is the
          only one there has ever been. The first rotation makes the designation required.
        */
        var founding = options.Value.FoundingChainKey;
        if (string.IsNullOrWhiteSpace(founding))
        {
            if (_keyRing.Count > 1)
            {
                throw new AuditKeyRingException(
                    "Audit:FoundingChainKey is required once a key has been retired. Rows written "
                    + "before the key-identity column record no identity, and verifying them under "
                    + "whatever Audit:ChainKey holds today would silently re-attribute history to a "
                    + "key that did not write it.");
            }

            // Nothing retired, so the current key is the founding key and has no epoch to end.
            _foundingKey = options.Value.ChainKey;
            _foundingLastSequence = null;
            _foundingFirstSequence = 1;
        }
        else if (!_keyRing.ContainsKey(DeriveKeyId(founding)))
        {
            throw new AuditKeyRingException(
                "Audit:FoundingChainKey names a key that is neither Audit:ChainKey nor one of "
                + "Audit:RetiredChainKeys. It is a designation, not a second copy — the key's "
                + "material must live in exactly one place.");
        }
        else
        {
            _foundingKey = founding;

            // ITS EPOCH COMES FROM THE RING ENTRY, because the founding key is a DESIGNATION of a
            // key already in the ring rather than a second copy -- so whatever bound that entry
            // carries is the bound here too. Null when the designation points at the current key,
            // which is unbounded by definition.
            var foundingEntry = _keyRing[DeriveKeyId(founding)];
            _foundingLastSequence = foundingEntry.LastSequence;
            _foundingFirstSequence = foundingEntry.FirstSequence;
        }
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
            /*
              ⚠️ THE OVERFLOW GUARD FOUR COMMITS AGO WENT ON THE WRONG NUMBER. It refuses a CONFIGURED
              LastSequence of long.MaxValue, and the configured number is the one an operator types.
              The number that matters here is the STORED tail, and that is the one an attacker with
              write access controls.

              MEASURED: plant a single row at long.MaxValue and the next honest write receives
              long.MinValue, because ++ is unchecked. Sequence is the column the walk ORDERS BY, so
              every row written afterwards sorts BELOW the entire history, and the verdict becomes
              LinkBroken with nothing verified. One UPDATE, and the trail reads as destroyed.

              Refusing is the D1 trade, deliberately: an audit write that cannot be made honestly
              fails the business action rather than being made dishonestly. It also leaves the
              planted row in place as the evidence of what happened, which a wrap does not.
            */
            if (sequence == long.MaxValue)
            {
                throw new InvalidOperationException(
                    $"The audit trail's tail is at sequence {sequence}, the largest a sequence can "
                    + "be, so the next row has nowhere to go. Nothing this deployment writes reaches "
                    + "that value: it is assigned as tail + 1 from an empty table. A tail there was "
                    + "PUT there, and continuing would wrap the next sequence to the bottom of the "
                    + "range and reorder the whole trail beneath it. Preserve the table and "
                    + "escalate.");
            }

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

            // The CURRENT key, always. A retired key is read-side only: it exists so its rows stay
            // verifiable, never so it can write another one.
            row.RowHash = ComputeRowHash(row, _options.Value.ChainKey);
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

            /*
              THE ROW NAMES ITS KEY AND THE RING LOOKS IT UP. It does not try keys in turn, and the
              difference is the whole safety of rotating: a trial-keyring verifier accepts a row a
              RETIRED key could have minted at any sequence, so every rotation would widen the
              forgery surface instead of narrowing it. The id is inside the hashed payload, so a row
              cannot lie about which key to check it with without breaking the hash that check
              produces.

              ⚠️ A 'v2' ROW SELECTS NOTHING, and this is where that shows. That version records no
              key identity, so there is no id to look up: those rows are checked under the FOUNDING
              key and no other, which is why that key has to be named rather than assumed. They are
              not stranded by a rotation -- naming the founding key is exactly what keeps them
              verifiable -- but they can never be rotated either, because rotation needs a per-row
              identity to select on. That is not an oversight in the ring; it is why the tail-anchor
              decision required KeyId as a stored column BEFORE the first anchor rather than
              alongside rotation.

              THE FOUNDING KEY'S EPOCH BINDS THEM AT BOTH ENDS, like every other. Above it a 'v2'
              row is minting -- that version is the one shape that reaches the founding key without
              naming it. Below it the reading is entirely different and is not an attack at all:
              identity-less rows are the OLDEST rows there are, so an epoch starting above them
              means the designation names a ring member that is not the oldest. The two get separate
              verdicts below, because their remedies have nothing in common.

              (This comment said "checked under the current key alone -- meaning a rotation strands
              every legacy row", and the founding key that makes both halves false landed in the
              SAME commit, fifteen lines below it. Not a sentence outlived by later work: one that
              was already false when it was written, in the same diff as the code contradicting it.
              (The distance was given twice and given differently -- "fifteen lines below" here and
              "three lines above" one clause later -- so one of the two had to be wrong. Measured in
              9ea4e80: the sentence ends on line 823, selectedKey = _foundingKey is on 838.) Raised
              in review on 9ea4e80.)
            */
            string? selectedKey;

            /*
              ⚠️ TWO WAYS TO HAVE NO KEY, AND THEY NEED OPPOSITE REMEDIES, so they are not allowed to
              produce the same sentence. Both used to land on `selectedKey = null` and report "no key
              in this ring has that id" -- which is FALSE for the second one, because the ring does
              have it.

              UNKNOWN ID: the key that wrote this row was never retired into the configuration. Add
              it, and the row verifies.

              EXPIRED BOUNDARY: the ring holds that key, and the row sits ABOVE the sequence the key
              stopped writing at. The remediation is the opposite of the first one and the wrong move
              is available: raising LastSequence can make the verdict go green, and if the row was
              minted after the retirement, raising it is completing the attack. So the message has to
              name the boundary and both readings rather than send anybody to the config.

              ⚠️ "MAKES THE VERDICT GO GREEN" WAS UNCONDITIONAL HERE AND IN BOTH VERDICT STRINGS.
              It is conditional in the minting reading: raising this entry's boundary also raises the
              NEXT epoch's start, because a start is derived as the previous boundary plus one, so
              every row the newer key wrote in the range just handed back is refused and the break
              moves DOWN rather than clearing. It goes green only when that range is empty -- which
              is the state a deployment sits in between the rotation and the newer key's first
              write, and the state the minting attack needs.
            */
            long? expiredBoundary = null;
            long? unbegunBoundary = null;

            if (row.PayloadVersion == CurrentPayloadVersion)
            {
                if (row.KeyId is not null && _keyRing.TryGetValue(row.KeyId, out var found))
                {
                    // A correct hash under a key that had stopped writing by this sequence is not
                    // history -- it is what minting looks like. The same is true underneath: a key
                    // that had not STARTED by this sequence did not write here either, and a ring
                    // that only bounded the top let the newest retired key re-author everything
                    // below it.
                    if (found.LastSequence is { } last && row.Sequence > last)
                    {
                        selectedKey = null;
                        expiredBoundary = last;
                    }
                    else if (row.Sequence < found.FirstSequence)
                    {
                        selectedKey = null;
                        unbegunBoundary = found.FirstSequence;
                    }
                    else
                    {
                        selectedKey = found.Key;
                    }
                }
                else
                {
                    selectedKey = null;
                }
            }
            else
            {
                /*
                  The FOUNDING key, not the current one. See AuditOptions.FoundingChainKey: a null
                  identity means none was recorded, and re-pointing those rows at whatever is current
                  is the one thing ADR-0044 named and refused in advance.

                  ⚠️ AND BOUNDED BY THE SAME EPOCH, because the forger picks the payload version.
                  Selecting by KeyId bounds every key a row can NAME; a 'v2' row names none, so
                  labelling a minted row 'v2' reached the founding key without naming it and skipped
                  the bound. Measured: such a row, at the tail, above the boundary, verified clean.
                  A boundary that one payload version can walk around is not a boundary.
                */
                if (_foundingLastSequence is { } foundingLast && row.Sequence > foundingLast)
                {
                    selectedKey = null;
                    expiredBoundary = foundingLast;
                }
                else if (row.Sequence < _foundingFirstSequence)
                {
                    /*
                      TWO SHAPES REACH THIS, AND THEY NEED OPPOSITE RESPONSES.

                      (a) Audit:FoundingChainKey designates something other than the OLDEST key in
                          the ring. Identity-less rows are the oldest rows there are, so a founding
                          key whose epoch starts above them is a designation that cannot mean what it
                          says, and saying so is better than checking those rows under it.

                      (b) ⚠️ THE ROW'S SEQUENCE IS BELOW 1. On a deployment that has never rotated
                          _foundingFirstSequence is hard-set to 1, so this branch fires for any 'v2'
                          row stored at 0 or below -- with ONE key in the ring and no designation
                          configured at all. The comment here used to say (a) was the only way in.
                          Link() assigns `++sequence` from a tail of 0, so nothing this deployment
                          writes can produce a non-positive sequence and no CHECK constraint stops
                          one being inserted; it is a modification, and re-pointing a designation
                          that does not exist cannot change the verdict. At the base commit the same
                          row reached the hash and came back HashMismatch, which told the operator to
                          preserve the table and escalate -- so getting this wrong is a regression
                          against what the tool used to say, not merely a gap.

                      The arm below picks between them on the sequence, which it already has.
                    */
                    selectedKey = null;
                    unbegunBoundary = _foundingFirstSequence;
                }
                else
                {
                    selectedKey = _foundingKey;
                }
            }

            // NO VERSION GATE HERE. It used to read `PayloadVersion == CurrentPayloadVersion &&
            // selectedKey is null`, which was safe only while the v2 arm could not produce null.
            // Now that it can, the gate would let a refused row fall through to a hash comparison
            // against a null key -- turning a security refusal into a crash.
            if (selectedKey is null)
            {
                /*
                  SEVEN WAYS TO HAVE NO KEY, AND NO TWO OF THEM TAKE THE SAME ACTION. Four are a
                  product of two axes: the row is ABOVE the epoch or BELOW it, and it either NAMES a
                  key or records no identity and is answered for by Audit:FoundingChainKey. The
                  fifth is a row that names an id the ring does not hold; the sixth is a row on the
                  current version carrying no id at all, which is the mirror of the 'v2' row carrying
                  one and is refused higher up. The seventh is not a boundary at all -- an
                  identity-less row stored BELOW sequence 1, which reaches the same comparison
                  because the founding epoch starts at 1 on a ring that has never rotated, and which
                  no setting can fix.

                  (This said FIVE, then SIX, each time in the commit that added the arm. Counted from
                  the arms below, not from memory.)

                  They are spelled out separately because collapsing any pair produces a sentence
                  that is false for one of them and points its reader at the wrong setting -- which
                  on the minting readings means the prescribed fix completes the attack.

                  ⚠️ THIS SAID THREE, AND THE COLLAPSE IT WARNS ABOUT WAS SITTING IN IT. The count was
                  written when the boundary had one end. Giving the epoch a start added two paths and
                  a single arm was left serving both, so a row that names NOTHING was told it "names
                  a key" and sent to edit a LastSequence that cannot move it.

                  ⚠️ THE COUNT COMES FROM THE ARMS, AND THE DERIVATION WRITTEN HERE ONCE SAID
                  OTHERWISE. This block claimed it enumerated them "from the assignments to
                  selectedKey above, not from the arms below", while eleven lines higher it said the
                  opposite. The assignments give FIVE: two of them each serve two situations that the
                  tuple separates downstream -- a 'v3' row naming an id the ring does not hold versus
                  one carrying no id at all, and an identity-less row below the founding epoch by
                  DESIGNATION versus one below sequence 1. So the arms are the only place the number
                  can be counted, which is the procedure this comment claimed to have avoided.
                  Counting them is safe BECAUSE the five assignments are reconciled against them:
                  five assignments, seven arms, and both splits are named above.
                */
                /*
                  ⚠️ BLANK IS NOT AN IDENTITY, AND THE TUPLE USED row.KeyId RAW. A 'v3' row whose
                  KeyId column was emptied rather than nulled is not null, so it missed the
                  records-none arm and fell through to the default one -- which told the operator a
                  key was missing from Audit:RetiredChainKeys and to go and add it. The right reading
                  is the opposite: the column is inside the hashed payload, nothing this deployment
                  writes leaves it empty on this version, so the value was removed after the fact.
                  Normalised here rather than at the lookup, because _keyRing.TryGetValue fails on
                  either shape and only the VERDICT needs to tell them apart.
                */
                var recordedIdentity =
                    string.IsNullOrWhiteSpace(row.KeyId) ? null : row.KeyId;

                var reason = (expiredBoundary, unbegunBoundary, recordedIdentity) switch
                {
                    // NAMES a key, BELOW that key's epoch. The mirror of the expired case and the
                    // one that made the boundary half a boundary: a key answering from the bottom of
                    // the table meant compromising the NEWEST retired key handed over the history.
                    (null, { } start, not null) =>
                        $"Row {row.Id} is sequence {row.Sequence:N0} and names key id "
                        + $"'{row.KeyId}', whose epoch begins at {start:N0} — so it names a key that "
                        + "had not started writing when this row was written. Its hash was NOT "
                        + "checked. An earlier key wrote this stretch; a row here that claims a later "
                        + "one is either a boundary recorded too HIGH for that earlier key, or a row "
                        + "RE-AUTHORED by whoever holds the later one. ⚠️ Epochs are derived from the "
                        + "recorded boundaries, so moving one moves two — check the rotation record "
                        + "before changing anything.",

                    /*
                      NAMES NOTHING, below the FOUNDING key's epoch — and this arm exists because the
                      one above was serving both. A 'v2' row records no key identity, so the sentence
                      "names a key" was false for it, and worse, both remedies it offered were the
                      wrong ones: _foundingFirstSequence is INHERITED from the designated entry, so
                      the only boundary that moves it is the PRECEDING entry's, and moving that
                      cannot lower the start past the designation's position in boundary order. An
                      operator following the old verdict would have
                      changed retirement boundaries — which the same sentence warns changes verdicts
                      for rows they did not think they were touching — while the actual
                      misconfiguration stayed exactly where it was.

                      There is only one way to get here, and it is not an attack: the designation
                      names a ring member that is not the OLDEST, so the epoch it opens starts above
                      the identity-less rows, which are the oldest rows there are.
                    */
                    (null, { } start, null) when row.Sequence < 1 =>
                        $"Row {row.Id} is a '{LegacyPayloadVersion}' row stored at sequence "
                        + $"{row.Sequence:N0}, which is below the first sequence this trail can "
                        + "have. Its hash was NOT checked. ⚠️ THIS IS NOT A CONFIGURATION PROBLEM. "
                        + "Writing assigns each row the tail plus one, starting from 1, so nothing "
                        + "this deployment writes lands here and no key needs to be held to put it "
                        + "there — the row was inserted. Do NOT edit Audit:FoundingChainKey or any "
                        + "boundary: on a deployment that has never rotated the founding epoch "
                        + "starts at 1 whatever you point it at, and this row is below 1 either way. "
                        + "Preserve the table and escalate.",

                    (null, { } start, null) =>
                        $"Row {row.Id} is a '{LegacyPayloadVersion}' row, which records no key "
                        + "identity, so it is checked under Audit:FoundingChainKey — and the epoch "
                        + $"that key opens begins at {start:N0}, while this row is sequence "
                        + $"{row.Sequence:N0}. Its hash was NOT checked. Rows recording no identity "
                        + "are the OLDEST rows there are, so a founding key whose epoch starts above "
                        + "them is a designation that cannot mean what it says. ⚠️ The fix is "
                        + "Audit:FoundingChainKey — point it at the OLDEST key in the ring. Lowering "
                        + "the boundary of the entry BEFORE it would move this epoch's start, "
                        + "because the start is derived from that boundary — but it cannot move it "
                        + "far enough. Boundaries are at least 1 and strictly increase, so this "
                        + "epoch's start cannot fall below the designation's POSITION in that order: "
                        + "2 for the second key, 3 for the third. The edit clears this row only if "
                        + "its sequence is at or above that number, and identity-less rows are the "
                        + "OLDEST there are — so if it clears, the trail does not begin at sequence "
                        + "1, which is a second finding rather than a fix.",

                    // Named a key the ring holds, above that key's epoch.
                    ({ } bound, _, not null) =>
                        $"Row {row.Id} names key id '{row.KeyId}', which this verification DOES "
                        + $"hold — but that key was retired at sequence {bound:N0} and this row is "
                        + $"sequence {row.Sequence:N0}. Its hash was NOT checked. Two readings, and "
                        + "they need opposite responses: either the recorded boundary is too LOW "
                        + "and this row is genuine history, or the row was MINTED with a retired "
                        + "key after the rotation. ⚠️ Raising LastSequence can turn this verdict "
                        + "green under either reading, and going green does not tell you which was "
                        + "true — so establish which it is from the rotation record "
                        + "before touching the configuration.",

                    // Named NOTHING, above the founding key's epoch. The dangerous one: the v2
                    // payload records no key identity, so this is how a forger reaches the founding
                    // key without naming it, and the boundary is the only thing that sees it.
                    ({ } bound, _, null) =>
                        $"Row {row.Id} is a '{LegacyPayloadVersion}' row, which records no key "
                        + "identity, so it is checked under Audit:FoundingChainKey — and that key "
                        + $"was retired at sequence {bound:N0} while this row is sequence "
                        + $"{row.Sequence:N0}. Its hash was NOT checked. A row above the founding "
                        + "key's boundary that names no key is the one shape that reaches that key "
                        + "without naming it, so treat MINTING as the leading reading here rather "
                        + "than as the alternative. The benign reading is that the boundary is too "
                        + "LOW. ⚠️ Raising LastSequence can turn this verdict green under either "
                        + "reading, and going green does not tell you which was true — establish "
                        + "which it is from the rotation record, outside this database, before "
                        + "touching the configuration.",

                    /*
                      Declares the CURRENT version, which has a place to record a key identity, and
                      carries none. The exact mirror of the 'v2' row carrying an id, refused higher
                      up: one version has nowhere to keep the value, the other has nowhere to get it
                      from, and neither is something the writer produces.
                    */
                    (null, null, null) =>
                        $"Row {row.Id} declares payload version '{CurrentPayloadVersion}', which "
                        + "records the identity of the key that wrote it, and records none. Its hash "
                        + "was NOT checked: there is nothing to select a key by. Nothing this "
                        + "deployment writes leaves that column empty on this version, so the value "
                        + "was removed after the fact — and the column is inside the hashed payload, "
                        + "which makes removing it a modification rather than a configuration note.",

                    // Names an id the ring does not hold at all.
                    _ =>
                        $"Row {row.Id} was written under key id '{row.KeyId}' and no key "
                        + $"in this verification's ring has that id — it holds '{_keyId}'"
                        + (_keyRing.Count > 1
                            ? $" and {_keyRing.Count - 1} retired key(s)"
                            : " and no retired keys")
                        + ". Its hash was NOT checked. Either the key that wrote this row was never "
                        + "added to Audit:RetiredChainKeys, or the column was overwritten. Which one "
                        + "is NOT positional -- a missing key fails over its own epoch and nothing "
                        + "above it, so it breaks in the middle with verified rows beneath, which is "
                        + "what a write looks like as well. To tell them apart, run again with that "
                        + "key in the ring: the id above says WHICH retired key you need — it is "
                        + "derived from the material and cannot be pasted back — so take that key "
                        + "from wherever they are kept, add it as Audit:RetiredChainKeys:N:Key with "
                        + "LastSequence from the rotation record, and set Audit:FoundingChainKey, "
                        + "which becomes required as soon as anything is retired. A configuration "
                        + "miss clears. A write does not.",
                };

                return new AuditChainVerification(
                    verified,
                    row.Sequence,
                    reason,
                    lowest,
                    highest,
                    AuditChainBreakKind.UnknownScheme,
                    row.PayloadVersion,
                    row.KeyId,
                    _keyId);
            }

            var expected = ComputeRowHash(row, selectedKey!);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(row.RowHash), Encoding.ASCII.GetBytes(expected)))
            {
                /*
                  THE LAST THREE ARGUMENTS ARE NOT OPTIONAL HERE, whatever their defaults say. The
                  tool decides what to tell an operator by reading them, so a verdict that omits
                  them is a verdict that silently answers "no version, no key identity" -- and the
                  branch meant to exonerate a confirmed key then never runs. Measured: it did not,
                  and two tests were green over it, because both built the record by hand instead of
                  taking one from this method.
                */
                /*
                  "NAMES A KEY" AND "THE RING SELECTED ITS KEY" ARE THE SAME CONDITION HERE, and the
                  two guards above are what make them the same. A 'v2' row carrying a key id is
                  refused before this point, and a 'v3' row whose id the ring cannot select returns
                  above -- so a row reaching the hash comparison with a non-null KeyId is exactly a
                  row the ring answered for. Reading it off KeyId keeps the condition next to the
                  sentence it justifies.
                */
                var confirmed = row.KeyId is not null;
                return new AuditChainVerification(
                    verified,
                    row.Sequence,
                    $"Row {row.Id} does not match its own hash. "
                    + (confirmed
                        ? "It was altered after it was written. The key is not in question: this "
                          + "row names a key id and the verification ring SELECTED that key by "
                          + "that id — which after a rotation is usually a RETIRED key rather than "
                          + "Audit:ChainKey — and a row naming an id the ring cannot select is "
                          + "refused before its hash is ever recomputed."
                        : "Either it was altered after it was written, or this verification holds "
                          + "different key material from the one that wrote it. This row records "
                          + "no key identity, so it was checked under Audit:FoundingChainKey — "
                          + "which is Audit:ChainKey only while nothing has been retired — and the "
                          + "two cannot be told apart here."),
                    lowest,
                    highest,
                    AuditChainBreakKind.HashMismatch,
                    row.PayloadVersion,
                    row.KeyId,
                    _keyId);
            }

            previous = row.RowHash;
            verified++;
        }

        /*
          THE TAIL HASH COMES FROM THE WALK, and this is the only place it can honestly come from.
          Asking the database for it separately would be a SECOND instant: a row committed between
          the walk and that read makes the count and the hash describe two different tables, which is
          how a state that never existed gets anchored. `previous` already holds the last verified
          row's hash, so this costs nothing and is taken from the same read as the count and range.

          Null on every break above, deliberately: the hash of a tail this walk did not certify must
          never be anchorable.
        */
        return new AuditChainVerification(
            verified, null, null, lowest, highest, TailRowHash: previous);
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
    private static string ComputeRowHash(AuditEvent row, string key) => row.PayloadVersion switch
    {
        CurrentPayloadVersion => Hmac(key, string.Join('|',
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
        LegacyPayloadVersion => Hmac(key, string.Join('|',
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

    // The key is a PARAMETER rather than a field read, because the write path and the verify path
    // now disagree about which key is correct: writing is always the current one, verifying is
    // whichever key the row names. A method that reached for _options here would silently make every
    // historical row fail after a rotation.
    private static string Hmac(string key, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}
