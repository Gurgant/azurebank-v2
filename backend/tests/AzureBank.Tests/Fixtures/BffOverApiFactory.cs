extern alias bff;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Yarp.ReverseProxy.Forwarder;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// The real BFF in front of the real API, in one process: both of the BFF's roads to the API — its
/// own <c>BackendApi</c> client and the YARP proxy — are delivered to the
/// <see cref="CustomWebApplicationFactory"/>'s test server instead of to a socket.
/// </summary>
/// <remarks>
/// <para>
/// Nothing on either side is scripted. The BFF's own handlers still run (the service key is added
/// by its <c>ServiceCredentialHandler</c> and by its proxy transform), and the API answers with its
/// own middleware, controllers and database. Written for ADR-0057 §10 O0-2 items 3 and 6, whose
/// claims each span both hosts: what the browser's "Esci" makes the BFF send, and what the API then
/// writes; what the API answers a key it does not hold, and what the BFF hands the browser.
/// </para>
/// <para>
/// <b>Testing, not Development.</b> <c>WebApplicationFactory</c> defaults to Development, which
/// loads the <c>azurebank-bff</c> user secrets of whatever machine runs the suite, and a key there
/// could decide which key the BFF sends. The session cookie therefore carries its <c>__Host-</c>
/// prefix; read the name from <c>BffSessionOptions</c>, never write it.
/// </para>
/// <para>
/// Both test servers leave <c>Connection.RemoteIpAddress</c> null, so a call from this BFF reaches
/// the API with no address at all.
/// </para>
/// </remarks>
public sealed class BffOverApiFactory(CustomWebApplicationFactory api, string serviceKey)
    : WebApplicationFactory<bff::Program>
{
    private FakeTimeProvider? _clock;
    private bool _demo;

    /// <summary>
    /// Gives the BFF a <see cref="FakeTimeProvider"/> and returns it. Call before the host starts.
    /// </summary>
    /// <remarks>
    /// In the BFF's own code only the session-stamp watcher reads <see cref="TimeProvider"/>
    /// (ADR-0057 §5.3), so this moves its 15 s period: sessions, the revoker and the sweep read the
    /// wall clock.
    /// </remarks>
    public FakeTimeProvider UseFakeClock()
    {
        _clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        return _clock;
    }

    /// <summary>
    /// Turns the public demo on in the BFF: <c>Demo:Enabled</c>. Call before the host starts.
    /// </summary>
    /// <remarks>
    /// The BFF's flag only: the API behind it has its own, which
    /// <see cref="CustomWebApplicationFactory.EnableDemo"/> sets.
    /// </remarks>
    public void EnableDemo()
    {
        _demo = true;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ServiceCredential:BffKey", serviceKey);
        if (_demo)
        {
            builder.UseSetting("Demo:Enabled", "true");
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("BackendApi")
                .ConfigurePrimaryHttpMessageHandler(() => api.Server.CreateHandler());
            services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(new ToTheApi(api)));
            if (_clock is not null)
            {
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
            }
        });
    }

    /// <summary>YARP's outbound client, pointed at the API's test server.</summary>
    private sealed class ToTheApi(CustomWebApplicationFactory api) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) =>
            new(api.Server.CreateHandler(), disposeHandler: true);
    }
}
