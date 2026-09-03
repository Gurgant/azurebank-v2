using System.Globalization;
using System.Text;

namespace AzureBank.AuditVerifier.Notices;

/// <summary>
/// Writes each notice as one RFC 5322 message file (<c>.eml</c>) into a directory the operator named.
/// </summary>
/// <remarks>
/// <para>
/// A PICKUP DIRECTORY, NOT A RELAY. Mail servers have collected from directories like this one for
/// thirty years (Postfix's pickup, the IIS SMTP pickup folder), and a mail client opens the file as
/// the message it is — which is what makes it the honest last hop for a deployment with no relay:
/// the artefact is real and complete, and what is missing is the thing that moves it. Until
/// something does, the notice has reached the edge of this machine and nobody else
/// (<c>ExportCommand</c> says the same of an anchor file, for the same reason).
/// </para>
/// <para>
/// CREATED EXCLUSIVELY, the export idiom: <c>FileMode.CreateNew</c> refuses an existing path
/// atomically, so a second run aimed at the same directory cannot overwrite the copy the first run
/// left, and the refusal surfaces as an <see cref="IOException"/> the command reports as
/// NOT NOTIFIED with the row still owed. No BOM, CRLF line endings: what RFC 5322 expects.
/// </para>
/// <para>
/// The file is named by the notice reference, never by the address; the address appears inside the
/// file, in the <c>To:</c> header, because the file IS the message. That makes the directory a
/// spool of addresses at rest, which is why the command refuses a directory inside a git repository
/// and the runbook says to delete the spool after the demonstration.
/// </para>
/// </remarks>
public sealed class PickupDirectoryTransport : INoticeTransport
{
    /// <summary>
    /// A domain reserved by RFC 2606 for exactly this: it resolves nowhere and impersonates nobody.
    /// A real-looking domain in a demonstration file would be the opposite of both.
    /// </summary>
    public const string Sender = "no-reply@azurebank.invalid";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public async Task<string> DeliverAsync(
        RenderedNotice notice, string toAddress, string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, notice.FileName);
        var bytes = Utf8NoBom.GetBytes(Compose(notice, toAddress, DateTime.UtcNow));

        // The directory is NOT created here: a missing one is the operator's mistake, and it lands
        // in the write-failure branch as a DirectoryNotFoundException rather than being papered over.
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(bytes, cancellationToken);
        await file.FlushAsync(cancellationToken);

        return notice.FileName;
    }

    /// <summary>
    /// The message text, headers first, exactly as a mail client expects to read it.
    /// </summary>
    internal static string Compose(RenderedNotice notice, string toAddress, DateTime nowUtc)
    {
        var headers = new StringBuilder()
            .Append("From: ").Append(Sender).Append("\r\n")
            .Append("To: ").Append(toAddress).Append("\r\n")
            .Append("Subject: ").Append(notice.Subject).Append("\r\n")
            .Append("Date: ").Append(nowUtc.ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture)).Append(" +0000\r\n")
            .Append("Message-ID: <").Append(notice.MessageId).Append("@azurebank.invalid>\r\n")
            .Append("MIME-Version: 1.0\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append("Content-Transfer-Encoding: 8bit\r\n")
            .Append("Auto-Submitted: auto-generated\r\n")
            .Append("\r\n");

        return headers.Append(notice.Body.Replace("\n", "\r\n", StringComparison.Ordinal)).ToString();
    }
}
