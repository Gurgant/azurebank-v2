using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 413: a request body exceeds the endpoint's limit outside the idempotency protocol.
/// </summary>
public sealed class PayloadTooLargeException : AppException
{
    public const string Detail = "The request body exceeds the 32 KB limit for this endpoint.";

    public PayloadTooLargeException()
        : base(Detail, ErrorCodes.PayloadTooLarge, 413)
    {
    }
}
