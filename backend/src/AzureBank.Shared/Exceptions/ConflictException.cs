namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 409 - Conflict (e.g., duplicate AzureTag, email)
/// </summary>
public class ConflictException(string message, string errorCode = "CONFLICT")
    : AppException(message, errorCode, 409)
{
}