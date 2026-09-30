namespace AzureBank.Bff.Tests;

/// <summary>
/// Replaces the "BackendApi" named client's primary handler so BFF tests can script the
/// upstream API's response without running it. Registered per-test via
/// ConfigureTestServices + AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler.
/// </summary>
/// <remarks>
/// Two kinds of responder. The synchronous one answers at once and no token can interrupt it. The
/// asynchronous one receives the call's own cancellation token, so it can stand in for an API that
/// does not answer: it waits on that token, and the BFF's <c>BackendApi:TimeoutSeconds</c> is what
/// ends the wait, as it would against a real API that has stopped answering.
/// </remarks>
internal sealed class FakeBackendApiHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public FakeBackendApiHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : this((request, _) => Task.FromResult(responder(request)))
    {
    }

    public FakeBackendApiHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _responder(request, cancellationToken);
    }
}
