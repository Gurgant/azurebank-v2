using AzureBank.Infrastructure.Notices;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// An <see cref="INoticeTransport"/> that records what it was handed and writes nothing.
/// </summary>
/// <remarks>
/// The envelope — rendered notice plus the address it was for — is what a real transport would put
/// on the wire, so it is what the verb tests assert on. It can also be told to fail with an
/// exception whose message deliberately CONTAINS the address, so a test can prove the command
/// prints the exception's type and never its message.
/// </remarks>
public sealed class RecordingTransport : INoticeTransport
{
    public const string Receipt = "recorded.eml";

    public List<(RenderedNotice Notice, string ToAddress, string Directory)> Envelopes { get; } = [];

    /// <summary>When set, every delivery throws this instead of recording.</summary>
    public Exception? Fails { get; set; }

    public Task<string> DeliverAsync(RenderedNotice notice, string toAddress, string directory, CancellationToken cancellationToken)
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        Envelopes.Add((notice, toAddress, directory));
        return Task.FromResult(Receipt);
    }
}
