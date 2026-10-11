using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// The 404 of an external transfer to a handle no user the payer can pay holds:
/// <c>RECIPIENT_NOT_FOUND</c>.
/// </summary>
/// <remarks>
/// A type of its own because the code is its own. <see cref="NotFoundException"/> answers
/// <c>ACCOUNT_NOT_FOUND</c> whatever the resource, the payer's own account included, and a client
/// chooses what to say from the code. The sentence has the form of every other 404, and it carries
/// the handle as the payer typed it.
/// </remarks>
public sealed class RecipientNotFoundException : AppException
{
    /// <param name="azureTag">The handle the transfer names, as the request carries it.</param>
    public RecipientNotFoundException(string azureTag)
        : base($"Recipient with identifier '{azureTag}' was not found.", ErrorCodes.RecipientNotFound, 404)
    { }
}
