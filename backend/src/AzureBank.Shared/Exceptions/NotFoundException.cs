namespace AzureBank.Shared.Exceptions;


/// <summary>
/// The 404 every missing resource produces.
/// </summary>
/// <remarks>
/// <para>
/// This type used to carry a second constructor, <c>NotFoundException(string message, string
/// errorCode)</c>, and it was a trap rather than a feature. Overload resolution prefers the more
/// specific parameter type, so <c>new NotFoundException("Recipient", someString)</c> bound to
/// THAT one — message "Recipient", errorCode set to the caller's identifier — while the
/// identical-looking <c>new NotFoundException("Account", someGuid)</c> bound here and behaved
/// correctly.
/// </para>
/// <para>
/// Seven of the eight call sites passed a <see cref="Guid"/> and were right by luck. The eighth,
/// <c>TransferService</c>'s recipient lookup, passed an azureTag — a string — and shipped
/// <c>{"detail":"Recipient","errorCode":"&lt;whatever handle the user typed&gt;"}</c> to the client.
/// The frontend branches on <c>errorCode === 'ACCOUNT_NOT_FOUND'</c>, so its recipient-not-found
/// message was unreachable in production, and an unvalidated user string was being echoed into a
/// field every consumer treats as a closed enum.
/// </para>
/// <para>
/// No call site ever wanted the second overload; it existed only to be selected by accident. It is
/// deleted rather than fixed at the one call site, because deleting it makes the mistake
/// unrepresentable — a future <c>NotFoundException("Recipient", tag)</c> now binds here and is
/// correct. Anything needing a different code should throw the exception that owns that code.
/// </para>
/// </remarks>
public class NotFoundException : AppException
{
    /// <param name="resource">The resource NAME ("Account", "User", "Transaction", "Recipient").</param>
    /// <param name="identifier">
    /// The identifier that was not found. Deliberately <see cref="object"/>: it accepts a
    /// <see cref="Guid"/> and a <see cref="string"/> alike, and with the second constructor gone
    /// there is no longer another overload for a string to prefer.
    /// </param>
    public NotFoundException(string resource, object identifier)
        : base($"{resource} with identifier '{identifier}' was not found.",
               Constants.ErrorCodes.AccountNotFound, 404)
    { }
}
