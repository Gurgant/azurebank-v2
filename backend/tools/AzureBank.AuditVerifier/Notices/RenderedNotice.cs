namespace AzureBank.AuditVerifier.Notices;

/// <summary>
/// A notice as text, before any address is attached to it.
/// </summary>
/// <param name="MessageId">The notice id in <c>N</c> form: the reference the recipient quotes, and the <c>Message-ID</c>.</param>
/// <param name="Subject">One line, safe to read in a mailbox listing: names the service and what happened, nothing else.</param>
/// <param name="Body">Plain text, LF-separated; the transport chooses the line ending of its format.</param>
/// <param name="FileName">What the pickup-directory transport names the file: the reference, never the address.</param>
public sealed record RenderedNotice(string MessageId, string Subject, string Body, string FileName);
