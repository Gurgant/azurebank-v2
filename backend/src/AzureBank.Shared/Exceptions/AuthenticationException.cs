using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 401 - Authentication failed
/// </summary>
public class AuthenticationException : AppException
{
    public AuthenticationException(string message = "Authentication failed")
        : base(message, ErrorCodes.InvalidCredentials, 401) { }

    public AuthenticationException(string message, string errorCode)
        : base(message, errorCode, 401) { }
}