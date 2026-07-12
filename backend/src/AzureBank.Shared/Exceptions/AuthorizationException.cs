using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 403 - Authorization failed
/// </summary>
public class AuthorizationException(string message = "Access denied")
    : AppException(message, ErrorCodes.AccessDenied, 403)
{
}