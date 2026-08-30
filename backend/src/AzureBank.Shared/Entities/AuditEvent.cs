using AzureBank.Shared.Enums;

namespace AzureBank.Shared.Entities;

/// <summary>
/// One row per security event: the durable, tamper-evident record that the log stream is not
/// (ADR-0044).
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS. Before it, every <c>SecurityEvent …</c> line went to a single
/// <c>Serilog.Sinks.Console</c> sink — no file, no SIEM, no retention, no table. Measured on
/// <c>main</c>: the only always-on sink is the console and the optional OTLP collector is a
/// loopback, in-memory, anonymous-admin Grafana. So an audit event survived until the process
/// scrolled. Under PCI DSS v4 10.5.1 (12 months retained, 3 immediately available), AMLD Art. 40 and
/// PSD2 Art. 21 (5 years), that is not an audit trail. Note NIST SP 800-53 AU-11 is deliberately NOT
/// cited for a number: it assigns the period to the organisation and states none.
/// </para>
/// <para>
/// THE LOG IS NOT REPLACED. Two destinations, two jobs: the log stream is for DETECTION (alerting,
/// volume, short life) and this table is the EVIDENCE (queried rarely, kept for years). Neither
/// substitutes for the other, and the same event goes to both.
/// </para>
/// <para>
/// TAMPER-EVIDENCE, and what it does and does not buy. <see cref="RowHash"/> is a keyed HMAC over
/// the row's own content, so altering any column after the fact breaks it, and the key is not in the
/// database — the same posture as <see cref="StepUpAuthorization.BindingHash"/>. It therefore
/// detects an attacker with SQL access but not the application's secrets. It does NOT survive an
/// attacker who holds both, and it does not by itself prove that whole rows were not DELETED — that
/// is what <see cref="PreviousHash"/> is for, and the strategy for maintaining it is a separate
/// decision recorded in ADR-0044 (see the note on that member).
/// </para>
/// <para>
/// APPEND-ONLY IS A PROPERTY OF THE DESIGN, NOT YET OF THE ENGINE. SQL Server ledger tables enforce
/// it in the engine — measured on this machine, UPDATE and DELETE are refused with Msg 37359 and the
/// error is not even catchable — and that is a deliberate, deferred upgrade rather than a rejection.
/// It is deferred because ledger rows do NOT survive a rollback (measured: BEGIN TRAN / INSERT /
/// ROLLBACK leaves nothing, since APPEND_ONLY protects COMMITTED rows only), because its digest
/// verification needs an anchor outside the database, because it permits only nullable columns to be
/// added afterwards, and above all because the property would then be provable by the 38 SQL-Server
/// tests and by none of the ~585 that run on the InMemory provider. A guarantee that almost every
/// test is blind to is the "green and false" state this project treats as the worst one.
/// </para>
/// <para>
/// NO PII, DELIBERATELY. Only pseudonymous identifiers and event names go in here. That is the one
/// lever left for GDPR erasure against a store designed never to be purged: what was never written
/// needs no deleting, and the personal data stays in the business tables where it can be removed.
/// </para>
/// </remarks>
public class AuditEvent
{
    /// <summary>Surrogate key. UUIDv7, so it sorts by creation like every other id here.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Monotonic insertion order, assigned by the database. The chain's ordering depends on this
    /// rather than on <see cref="OccurredAt"/>, because two rows can share a timestamp and a clock
    /// can move backwards.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>When it happened (UTC). NIST SP 800-53 AU-3(b).</summary>
    /// <remarks>
    /// UTC BY CONVENTION, not by type: SQL Server's <c>datetime2</c> stores no kind, so a value read
    /// back from the database arrives as <see cref="DateTimeKind.Unspecified"/> however it was
    /// written. That is why <c>AuditChain</c> hashes this field as <c>Ticks</c> — a rendering that
    /// depends on the kind produced a hash that only verified before the first round trip.
    /// </remarks>
    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// The event name — always one of the <c>SecurityEvents</c> constants, never a literal. AU-3(a):
    /// what type of event occurred.
    /// </summary>
    public required string Event { get; set; }

    /// <summary>AU-3(e): the outcome. See <see cref="AuditOutcome"/> for why the four values.</summary>
    public AuditOutcome Outcome { get; set; }

    /// <summary>
    /// AU-3(f), the acting subject. NULL where the event is precisely that nobody could be
    /// identified — an unknown refresh token, a cross-site request refused before authentication.
    /// A null here is information, not a gap.
    /// </summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>
    /// AU-3(f) again, the object acted on: what kind of thing <see cref="SubjectId"/> names
    /// ("Account", "User", "RefreshToken"). Kept beside the id because an id alone does not say
    /// which table it belongs to, and this row must be readable years after the code changed.
    /// </summary>
    public string? SubjectType { get; set; }

    /// <summary>The object's identifier. PCI DSS v4 10.2.2: identity of the affected resource.</summary>
    public Guid? SubjectId { get; set; }

    /// <summary>
    /// Correlates the row with the request that produced it, and with the log line that reported it.
    /// The bare 32-hex form the rest of this codebase emits, not the W3C <c>00-…-…-01</c> form.
    /// </summary>
    public string? TraceId { get; set; }

    /// <summary>
    /// Event-specific detail as JSON, for the fields that exist on one event and not the others — an
    /// error code, an attempt count, a refused path. Structured rather than a sentence, because a
    /// sentence is not queryable and this table exists to be queried.
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Which rendering of the hashed payload wrote this row, and therefore which one a verifier
    /// must use to re-derive <see cref="RowHash"/>.
    /// </summary>
    /// <remarks>
    /// THIS COLUMN IS THE VERSION PREFIX, not a copy of it. The same string is stored here and
    /// joined as the first element of the hashed payload, so the column and the prefix cannot
    /// disagree: overwriting one changes the hash. Before this existed the prefix was the literal
    /// <c>v2</c> compiled into the hasher, which meant a payload change re-rendered EVERY historical
    /// row under the new scheme and reported the honest ones as tampering.
    /// <para>
    /// IT IS ASSIGNED BY THE CHAIN, NEVER BY THE CALLER — the value is the verifier's instruction
    /// for how to read the row, so it comes from the component that renders the payload rather than
    /// from the one that supplies the content.
    /// </para>
    /// </remarks>
    public string PayloadVersion { get; set; } = string.Empty;

    /// <summary>
    /// Non-secret identifier of the <c>Audit:ChainKey</c> that wrote this row, derived from the key
    /// itself. NULL on rows written before key identity existed.
    /// </summary>
    /// <remarks>
    /// DERIVED, NOT CONFIGURED, and the difference is the whole point. A configured identifier can
    /// name the wrong key and nothing detects it; a derived one lets a verifier confirm that the key
    /// it holds is the key that wrote the row, and say so when it does not.
    /// <para>
    /// NULL MEANS "NO KEY IDENTITY WAS RECORDED", not "some particular key". Such a row is verified
    /// under the FOUNDING key, which <c>Audit:FoundingChainKey</c> names — and that is
    /// <c>Audit:ChainKey</c> only while nothing has been retired, because until then it is the only
    /// key there has ever been. NULL asserts nothing, hashes nothing, and is the absence of a claim
    /// rather than a claim.
    /// </para>
    /// <para>
    /// This paragraph used to end "whatever adds a second key must add a ring entry for the founding
    /// key rather than silently re-point every historical row at whatever is current", as future
    /// work. That work landed with the key ring: the designation is REQUIRED as soon as anything is
    /// retired, it must name material already in the ring, and it INHERITS that entry's epoch — so a
    /// row recording no identity is refused above the founding key's boundary exactly as a keyed row
    /// is above its own.
    /// </para>
    /// </remarks>
    public string? KeyId { get; set; }

    /// <summary>
    /// Keyed HMAC-SHA256 over this row's content, lowercase hex. Alter any column afterwards and
    /// this stops matching.
    /// </summary>
    public required string RowHash { get; set; }

    /// <summary>
    /// The previous row's <see cref="RowHash"/>, which is what turns independent rows into a chain
    /// and makes a row removed from the MIDDLE detectable rather than merely a modified one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ THE QUALIFIER IS THE CLAIM, AND THIS SENTENCE CARRIED THE WITHDRAWN VERSION OF IT. It read
    /// "makes a DELETED row detectable", full stop. That is true of an interior row and false of the
    /// TAIL: verification only ever looks backwards, so deleting the last row — or the last thousand
    /// — leaves every surviving row hashing correctly and linking correctly, and needs no key at
    /// all. ADR-0044 withdrew the unqualified wording in its own text; this summary is where it went
    /// on living. What would close that end, and why neither control that would is built here, is in
    /// that record and in <c>docs/deferred/anchoring-the-audit-trail.md</c>.
    /// </para>
    /// <para>
    /// NULLABLE ON PURPOSE, AND THE DECISION IT WAS LEFT OPEN FOR HAS SINCE BEEN MADE — this
    /// paragraph still said it was open. Chaining every row to its predecessor at insert time
    /// requires the writer to read the current tail and append to it atomically, which serialises
    /// audit inserts and therefore serialises every business operation that writes one. The
    /// alternative was to leave this null on the hot path and have a verifier compute the chain over
    /// <see cref="Sequence"/> order at checkpoints, keeping writes concurrent but detecting a
    /// deletion only at the next checkpoint. ADR-0044 D3 took the first: the chain is applied in the
    /// <c>SaveChanges</c> funnel, so <c>AuditChain</c> assigns this at insert, unconditionally. The
    /// column stays nullable because the first row of a chain has no predecessor — not because the
    /// strategy is undecided.
    /// </para>
    /// </remarks>
    public string? PreviousHash { get; set; }
}
