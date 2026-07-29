namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 409 - Conflict (e.g., duplicate AzureTag, email)
/// </summary>
public class ConflictException(
    string message,
    string errorCode = Constants.ErrorCodes.Conflict)
    : AppException(message, errorCode, 409)
{
}