using System.ComponentModel.DataAnnotations;

namespace AzureBank.Shared.Options;

/// <summary>
/// Configuration for the audit trail (ADR-0044). Binds to the "Audit" section.
/// </summary>
public class AuditOptions
{
    /// <summary>Configuration section name in appsettings.json.</summary>
    public const string SectionName = "Audit";

    /// <summary>
    /// Server-side key for the HMAC-SHA256 row hash that chains the audit trail. MUST be configured
    /// (user-secrets / environment), never committed, and never stored in the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// KEYED, NOT A BARE DIGEST, and the reason is the same one that decided
    /// <see cref="StepUpOptions.BindingKey"/>: an unkeyed SHA-256 over a row whose fields are all
    /// enumerable — an event name from a fixed list of 17, two Guids, a timestamp — can be
    /// recomputed by anyone holding the table. They would not need to invert anything; they would
    /// hash the candidates and compare. A key they do not have is what makes recomputation
    /// impossible, and therefore what makes an altered row detectable.
    /// </para>
    /// <para>
    /// A SEPARATE key from StepUp's and Idempotency's, deliberately, for the reason those two are
    /// already separate from each other: one leaked key must not forge the other's answer.
    /// </para>
    /// <para>
    /// WHAT THIS DOES NOT DEFEND AGAINST, stated so the ADR does not have to overclaim: an attacker
    /// holding both the database and the application's secrets can rewrite a row and recompute the
    /// chain. TWO deferred controls close that, at DIFFERENT layers, so neither of them is "the"
    /// answer. SQL Server's ledger refuses the write at the engine — and, measured, does NOT require
    /// Azure: automatic digest upload does, but <c>sp_generate_database_ledger_digest</c> takes no
    /// destination at all. An RFC 3161 timestamp supplies time from outside, which is the only thing
    /// that constrains whoever holds every key, and that is the operator rather than the attacker
    /// this key is against. ⚠️ This paragraph used to name the ledger alone and call it "a digest
    /// anchored outside the system". The ledger's DIGEST is that; its ENFORCEMENT is not, and needs
    /// nothing outside this machine. Collapsing the two is what made one control read as the whole
    /// remedy, here and in ADR-0044, which now carries the argument in full.
    /// </para>
    /// </remarks>
    public string ChainKey { get; set; } = string.Empty;

    /// <summary>
    /// HMAC key authenticating each anchor RECORD, so a database-only attacker can delete anchors
    /// but cannot mint them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SEPARATE FROM <see cref="ChainKey"/> ON PURPOSE, and the reason is sharper than the general
    /// "one leaked key must not forge the other's answer". The anchor exists to constrain somebody
    /// who holds the database; the row chain already concedes that an attacker holding the database
    /// AND the application's secrets can rewrite a row and recompute it. If the anchor were MACed
    /// under the same key, the one adversary the anchor is built for would be the one adversary it
    /// cannot see, and the whole record would be decoration.
    /// </para>
    /// <para>
    /// WHAT IT DOES AND DOES NOT BUY, stated so no document has to overclaim later. It makes
    /// DELETING an INTERIOR anchor loud — the chain's own counter gaps and its links stop meeting,
    /// though a SUFFIX removal stays silent — while
    /// MINTING one requires this key. It does NOT constrain the operator, who holds it: on a
    /// single-machine deployment the person who can truncate the table is the person who can write
    /// honest-looking anchors over the result. That is why the anchor's value is the number an
    /// operator wrote down somewhere else, and why nothing here is called proof.
    /// </para>
    /// </remarks>
    public string AnchorKey { get; set; } = string.Empty;

    /// <summary>
    /// Chain keys this deployment has RETIRED, so rows written under them stay verifiable after a
    /// rotation. Empty until the first rotation, which is the state every deployment starts in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A ROTATION MUST NEVER REWRITE A ROW — the tail-anchor decision ratified that, and the reason
    /// is that re-hashing history would invalidate every anchor ever issued while being, in the
    /// database, the exact row-rewriting operation the anchor exists to detect. So the only way a
    /// rotated deployment can still verify its own past is to keep the retired key and select it per
    /// row.
    /// </para>
    /// <para>
    /// ⚠️ SELECTED BY <c>AuditEvent.KeyId</c>, NEVER TRIED IN TURN. The same decision names the
    /// hazard in one sentence: a verifier that trials a keyring lets a RETIRED key mint rows at any
    /// sequence forever, so the forgery surface grows with every rotation — which inverts the reason
    /// to rotate. The row states which key wrote it, that id is inside the hashed payload, and it is
    /// the id that picks the key. A row naming a key this deployment does not hold stays UNCHECKED
    /// and breaks the walk, exactly as before.
    /// </para>
    /// <para>
    /// ⚠️ EACH ENTRY CARRIES THE SEQUENCE IT STOPPED AT, and that bound is what stops the ring being
    /// a REGRESSION. Selection by id defeats a row that LIES about its key; it does nothing about a
    /// row MINTED with a retired key and labelled honestly. Measured before the bound existed: such
    /// a row, appended after the rotation, verified clean — a power the key's holder did not have
    /// before the ring, because a retired key used to verify nothing at all. See
    /// <see cref="RetiredChainKey"/>.
    /// </para>
    /// <para>
    /// Retiring a key does NOT let it write. Writing always uses <see cref="ChainKey"/>; these are
    /// read-side only.
    /// </para>
    /// <para>
    /// ⚠️ READ-SIDE-ONLY IS NOT WHAT KEEPS A RETIRED KEY FROM BEING USABLE, and this paragraph
    /// claimed it was — one paragraph under the one that disproves it. It stops OUR code writing
    /// through a retired key. It does nothing about somebody holding that key and a database
    /// connection, who computes an honest hash and inserts by raw SQL. The BOUNDARY above is what
    /// stops that, and only OUTSIDE the key's epoch: inside it — from one past the previous
    /// boundary up to its own — a retired key still mints rows that verify. Raised in review on
    /// 930495f; the epoch gained its lower end on 10e7c1b, and this sentence said "only ABOVE the
    /// boundary" until then.
    /// </para>
    /// </remarks>
    public IList<RetiredChainKey> RetiredChainKeys { get; set; } = [];

    /// <summary>
    /// Which key wrote the rows that record NO key identity. Empty until the first rotation, when it
    /// becomes required.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ ADR-0044 SETTLED THIS BEFORE THE RING EXISTED, in a sentence whose wording is the whole
    /// point: *"whatever adds a second key must add a ring entry for the FOUNDING key rather than
    /// silently re-point history at whatever is current."* Rows written before the key-identity
    /// column carry a null <c>KeyId</c>, and a null means "no identity was recorded", never "the
    /// current key". Verifying them under whatever <see cref="ChainKey"/> happens to hold today
    /// would make a rotation quietly re-attribute history — and then report every one of those rows
    /// as tampered, which is the failure mode the whole design exists to avoid.
    /// </para>
    /// <para>
    /// It is a DESIGNATION, not a second copy: the value must also be <see cref="ChainKey"/> or one
    /// of <see cref="RetiredChainKeys"/>, so there is exactly one place each key's material lives.
    /// Before any rotation it is left empty and the current key is the founding key by definition,
    /// because it is the only one there has ever been. Once a key is retired, leaving it empty is
    /// refused rather than assumed: the assumption would be silent and almost always wrong.
    /// </para>
    /// </remarks>
    public string FoundingChainKey { get; set; } = string.Empty;


    /// <summary>
    /// How long the chain's tail read may wait, in seconds, before the movement it belongs to is
    /// refused. Bounds the queue every money movement stands in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED, which is why this exists at all. The tail is read under <c>UPDLOCK, HOLDLOCK</c>,
    /// so the lock is global to the table and every audited save queues on it. Stalling one tail read
    /// for three seconds delayed a deposit on a DIFFERENT account, by a DIFFERENT user, by
    /// <b>3,073-3,089 ms across three runs</b> — essentially the whole hold. One slow audit store
    /// degrades the whole bank, not just the movement that touched it. Only the 30-second
    /// <c>CommandTimeout</c> bounded that, and it bounds the whole statement rather than the wait.
    /// </para>
    /// <para>
    /// <b>THIS VALUE ALSO BOUNDS READINESS.</b> Every check tagged <c>ready</c> is registered with
    /// this as its timeout, because a probe stricter than the money path would report an instance
    /// unhealthy — taking it out of rotation — over a wait that instance had been told to tolerate.
    /// Raising this therefore makes <c>/health/ready</c> patient by the same amount.
    /// </para>
    /// <para>
    /// FIVE SECONDS, and the number is a floor argument rather than a guess: the lock is held for the
    /// few statements between the tail read and the commit, which B2 measured at well under a
    /// millisecond per chained insert, so hundreds of concurrent movements drain inside it. It is six
    /// times shorter than the command timeout it replaces on this path, which is the improvement.
    /// Configurable because the test that proves the refusal fires needs a value it can hit quickly.
    /// </para>
    /// <para>
    /// A COMMAND timeout, deliberately, not <c>SET LOCK_TIMEOUT</c>. The latter is SESSION-scoped and
    /// would ride a pooled connection into every unrelated statement that borrows it afterwards; the
    /// command timeout lives on the <c>DbContext</c>, which is scoped to one request.
    /// </para>
    /// <para>
    /// <b>THE LOWER BOUND OF 1 IS LOAD-BEARING OUTSIDE THIS CLASS.</b> The same value is used as the
    /// timeout on every readiness check registration, and <c>HealthCheckRegistration.Timeout</c>'s
    /// setter throws <c>ArgumentOutOfRangeException</c> for anything <c>&lt;= TimeSpan.Zero</c> that
    /// is not exactly <c>Timeout.InfiniteTimeSpan</c> (verified against dotnet/aspnetcore v10.0.0).
    /// Relaxing this range to admit 0 would move that failure to the FIRST readiness probe — far
    /// from the change that caused it — instead of to startup, where <c>ValidateOnStart</c> catches
    /// it now.
    /// </para>
    /// </remarks>
    /*
      RANGE-VALIDATED, because the two invalid values fail in opposite and equally bad ways.
      ZERO is the dangerous one: ADO.NET reads CommandTimeout = 0 as NO LIMIT, so a plausible typo
      in configuration silently restores the unbounded thirty-second-plus queue this setting exists
      to remove — and nothing would say so, because the code would look like it was working.
      NEGATIVE fails loudly instead, throwing from SetCommandTimeout at the worst possible moment:
      mid-save, on the money path. Three hundred is an upper bound rather than a considered maximum;
      anything near it has already defeated the purpose.
    */
    [Range(1, 300, ErrorMessage =
        "Audit:TailTimeoutSeconds must be between 1 and 300. Zero means NO timeout in ADO.NET, "
        + "which would silently remove the bound; a negative value throws mid-save.")]
    public int TailTimeoutSeconds { get; set; } = 5;
}
