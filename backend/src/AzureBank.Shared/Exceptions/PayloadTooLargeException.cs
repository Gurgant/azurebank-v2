using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 413: a request body exceeds the endpoint's limit outside the idempotency protocol.
/// </summary>
public sealed class PayloadTooLargeException : AppException
{
    /// <summary>
    /// The one sentence a client is told. It says 32 KB because every action marked
    /// <c>[RefuseOversizedBody]</c> declares 32,768 bytes; the filter itself reads whatever limit
    /// the endpoint declares.
    /// </summary>
    public const string Detail = "The request body exceeds the 32 KB limit for this endpoint.";

    public PayloadTooLargeException()
        : base(Detail, ErrorCodes.PayloadTooLarge, 413)
    {
    }
}
