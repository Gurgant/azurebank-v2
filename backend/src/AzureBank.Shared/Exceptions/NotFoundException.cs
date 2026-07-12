namespace AzureBank.Shared.Exceptions;


public class NotFoundException : AppException
{
    public NotFoundException(string resource, object identifier)
        : base($"{resource} with identifier '{identifier}' was not found.",
               Constants.ErrorCodes.AccountNotFound, 404)
    { }

    public NotFoundException(string message, string errorCode)
        : base(message, errorCode, 404)
    { }
}