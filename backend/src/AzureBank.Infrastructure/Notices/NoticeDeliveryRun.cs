using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Infrastructure.Notices;

/// <summary>
/// What happened to one owed notice when a runner tried to deliver it.
/// </summary>
public enum NoticeOutcome
{
    /// <summary>Rendered, handed to the transport, and the row marked with the receipt.</summary>
    Delivered,

    /// <summary>The account holds no email; nothing rendered, the row stays owed.</summary>
    NoAddress,

    /// <summary>
    /// The account's email carries a line break or NUL and would inject a header; nothing
    /// rendered, the row stays owed.
    /// </summary>
    UnusableAddress,

    /// <summary>The row names an event this build has no renderer arm for; the row stays owed.</summary>
    Unrenderable,

    /// <summary>The transport refused or failed; the row stays owed and is retried later.</summary>
    TransportFailed,

    /// <summary>
    /// Another runner marked the row between this runner's read and its mark. The artefact this
    /// runner produced is a second copy — at-least-once, named rather than hidden (ADR-0048). Not
    /// owed: the row is marked, by somebody.
    /// </summary>
    MarkedByAnother,
}

/// <summary>
/// The result for one notice: the outcome, the receipt when delivered, whether the audit row the
/// notice belongs to was found, and the exception TYPE (never the message) when a transport failed.
/// </summary>
/// <remarks>
/// The address is not here, and the failure message is not here, for the reason the transport
/// interface gives: an I/O message can echo the path and a relay's refusal can echo the recipient.
/// Callers turn this into console lines or log lines; neither ever sees the address.
/// </remarks>
public sealed record NoticeResult(
    SubscriberNotice Notice,
    string Reference,
    NoticeOutcome Outcome,
    string? Receipt,
    bool AuditRowMissing,
    string? FailureType);

/// <summary>
/// Delivers ONE owed notice: reads the address, checks the evidence, renders, hands the message to
/// the transport, and marks the row under its concurrency token. The unit the operator verb and the
/// in-process relay share (ADR-0048), so the two cannot drift on what "delivered" costs.
/// </summary>
/// <remarks>
/// <para>
/// THE ADDRESS GOES ONE WAY. It is read from the account, handed to the transport for the
/// <c>To:</c> header, and returned nowhere — not in the result, not in an exception. The verb
/// prints the result; the relay logs it; the address is in neither.
/// </para>
/// <para>
/// THE MARK CLEARS THE LEASE. A row a relay claimed carries <c>LeasedUntil</c>/<c>LeasedBy</c>; the
/// same UPDATE that sets <c>DeliveredAt</c> and the receipt sets both back to null, so a delivered
/// row never reads as held. The UPDATE is fenced by <c>DeliveredAt</c> as before: a second runner
/// that loaded the row while it was owed loses with <see cref="DbUpdateConcurrencyException"/>,
/// which is reported as <see cref="NoticeOutcome.MarkedByAnother"/>.
/// </para>
/// <para>
/// JOINED BY (ACTOR, EVENT), NEVER BY TIME, and reported rather than refused: a notice whose audit
/// row is missing is still delivered — the account holder is not punished for the gap — and the
/// absence is the finding. Since ADR-0047 made a kind repeatable this check is an existence check
/// only; ADR-0047 records the limit and the backlog carries the exact join.
/// </para>
/// </remarks>
public sealed class NoticeDeliveryRun
{
    private readonly AzureBankDbContext _context;
    private readonly INoticeTransport _transport;

    public NoticeDeliveryRun(AzureBankDbContext context, INoticeTransport transport)
    {
        _context = context;
        _transport = transport;
    }

    /// <summary>
    /// Delivers one tracked, still-owed notice. The caller owns the query that found it and the
    /// scope the context lives in.
    /// </summary>
    /// <param name="notice">A row tracked by this run's context with <c>DeliveredAt</c> null.</param>
    /// <param name="contact">The repudiation contact the notice must carry (NIST SP 800-63B-4 §4.6).</param>
    /// <param name="directory">Where the transport delivers — the full path of the pickup directory.</param>
    /// <param name="nowUtc">The clock for the message's Date header; read once per run by the caller.</param>
    /// <param name="cancellationToken">Stops between steps; a mark already saved stays saved.</param>
    public async Task<NoticeResult> DeliverAsync(
        SubscriberNotice notice,
        string contact,
        string directory,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var reference = notice.Id.ToString("N");

        var address = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == notice.UserId)
            .Select(u => u.Email)
            .SingleOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(address))
        {
            return new NoticeResult(notice, reference, NoticeOutcome.NoAddress, null, false, null);
        }

        /*
          A LINE BREAK IN THE ADDRESS WOULD BECOME A HEADER. The address heads the message, and RFC
          5322 headers end at CRLF, so an email holding one — unreachable through registration,
          reachable by anybody who can write the table — would inject whatever followed it (a Bcc:,
          say). Refused here as unusable, and refused again by the transport in case a second caller
          ever skips this check.
        */
        if (address.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
        {
            return new NoticeResult(notice, reference, NoticeOutcome.UnusableAddress, null, false, null);
        }

        var backed = await _context.AuditEvents
            .AnyAsync(e => e.ActorUserId == notice.UserId && e.Event == notice.Event, cancellationToken);
        var auditRowMissing = !backed;

        RenderedNotice rendered;
        try
        {
            rendered = NoticeRenderer.Render(notice, contact, nowUtc);
        }
        catch (InvalidOperationException)
        {
            return new NoticeResult(notice, reference, NoticeOutcome.Unrenderable, null, auditRowMissing, null);
        }

        string receipt;
        try
        {
            receipt = await _transport.DeliverAsync(rendered, address, directory, cancellationToken);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // The TYPE only: an I/O message can echo the path, and a relay's can echo the recipient.
            return new NoticeResult(
                notice, reference, NoticeOutcome.TransportFailed, null, auditRowMissing, failure.GetType().Name);
        }

        // Per notice, read after the write: a batch rendered slowly would otherwise date every
        // file to the moment the run began rather than the moment each was produced.
        notice.DeliveredAt = DateTime.UtcNow;
        notice.DeliveryReceipt = receipt;
        notice.LeasedUntil = null;
        notice.LeasedBy = null;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _context.Entry(notice).State = EntityState.Detached;
            return new NoticeResult(notice, reference, NoticeOutcome.MarkedByAnother, receipt, auditRowMissing, null);
        }

        return new NoticeResult(notice, reference, NoticeOutcome.Delivered, receipt, auditRowMissing, null);
    }
}
