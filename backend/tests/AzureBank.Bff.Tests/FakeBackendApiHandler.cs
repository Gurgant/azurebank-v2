namespace AzureBank.Bff.Tests;

/// <summary>
/// Replaces the "BackendApi" named client's primary handler so BFF tests can script the
/// upstream API's response without running it. Registered per-test via
/// ConfigureTestServices + AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler.
/// </summary>
internal sealed class FakeBackendApiHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public FakeBackendApiHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_responder(request));
    }
}
