using System.Globalization;
using System.Text;

namespace AzureBank.Infrastructure.Notices;

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

        // The refusal to overwrite, first and by name: an existing file at the notice's path is an
        // earlier copy, and it is never truncated. (The move below refuses it too.)
        if (File.Exists(path))
        {
            throw new IOException("A file already exists at the notice's path; the earlier copy is kept.");
        }

        /*
          WRITTEN BESIDE, THEN PUBLISHED. The bytes go to a staging file next to the final name and
          are moved into place only once they are all on disk, so a write that fails or is cancelled
          half-way can never leave a truncated message under the name a relay would collect. The
          staging file is created exclusively (FileMode.CreateNew) and removed on any failure; the
          move refuses an existing target, which keeps the "never overwrite" property across the
          window between the check above and the publish. The directory is NOT created here: a
          missing one is the operator's mistake, refused by the command before anything is read.
        */
        var staging = StagingPathFor(path);
        try
        {
            await using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(bytes, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }

            File.Move(staging, path, overwrite: false);
        }
        catch
        {
            try
            {
                File.Delete(staging);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The staging file is the lesser evil: its name says it is partial.
            }

            throw;
        }

        return notice.FileName;
    }

    /// <summary>
    /// Where this invocation writes before it publishes: beside the final name, with a suffix that
    /// no other invocation shares.
    /// </summary>
    /// <remarks>
    /// UNIQUE PER CALL, and the reason is Unix. Two runs aimed at one notice would both derive the
    /// same staging name from the final one; the second's exclusive create fails, its cleanup then
    /// deletes the staging file — and on Unix, unlike Windows, deleting a file another process holds
    /// open succeeds — so the first run's move finds no source and the notice is lost by the run
    /// that was winning. A per-call suffix means the cleanup can only ever remove this call's own
    /// file, and the two runs meet, as intended, at the move.
    /// </remarks>
    internal static string StagingPathFor(string finalPath) =>
        $"{finalPath}.{Guid.NewGuid():N}.partial";

    /// <summary>
    /// The message text, headers first, exactly as a mail client expects to read it.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The address contains a line break, which would end the <c>To:</c> header and start another.
    /// The command refuses such an address before rendering; this is the second lock on the same door.
    /// </exception>
    internal static string Compose(RenderedNotice notice, string toAddress, DateTime nowUtc)
    {
        if (toAddress.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
        {
            throw new ArgumentException(
                "A recipient address cannot contain a line break: it would become a header.", nameof(toAddress));
        }

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
