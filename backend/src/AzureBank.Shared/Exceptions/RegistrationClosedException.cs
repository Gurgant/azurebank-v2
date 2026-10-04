using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 403: registration is closed on the public demo, where a visitor is handed a prepared copy and
/// nobody creates a user.
/// </summary>
public sealed class RegistrationClosedException : AppException
{
    /// <summary>The one sentence a client is told, whatever the request carried.</summary>
    public const string Detail = "Registration is closed on this demo.";

    public RegistrationClosedException()
        : base(Detail, ErrorCodes.RegistrationClosed, 403)
    {
    }
}
