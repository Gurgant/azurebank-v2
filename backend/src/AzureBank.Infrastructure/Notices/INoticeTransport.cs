namespace AzureBank.Infrastructure.Notices;

/// <summary>
/// The last hop: takes a rendered notice and the address it is for, and produces a receipt.
/// </summary>
/// <remarks>
/// <para>
/// THE ONLY PLACE THE ADDRESS GOES. The renderer never receives it, the command never prints it,
/// the notice row never stores it; the transport sees it because the message has to be addressed
/// to somebody, and a transport that echoes it — in a receipt, in an exception message, in a file
/// name — would put it where ADR-0017 says it must not be. Implementations return a receipt that
/// identifies the artefact, not the recipient.
/// </para>
/// <para>
/// One implementation ships, <see cref="PickupDirectoryTransport"/>, and it delivers to a directory
/// on this machine rather than to the subscriber. What would replace it is named in
/// <c>docs/deferred/relaying-the-enrolment-notice.md</c>; nothing here pretends to be it.
/// </para>
/// </remarks>
public interface INoticeTransport
{
    /// <summary>
    /// Delivers one notice to one address, into the place the operator named.
    /// </summary>
    /// <returns>A receipt for the row: for the pickup directory, the file name.</returns>
    /// <exception cref="IOException">The artefact could not be produced; the notice stays owed.</exception>
    Task<string> DeliverAsync(RenderedNotice notice, string toAddress, string directory, CancellationToken cancellationToken);
}
