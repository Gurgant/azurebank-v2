using System.Net;
using System.Net.Http.Json;
using System.Text;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The BFF has two roads to the API — the YARP proxy for <c>/api/*</c> and the named
/// <c>BackendApi</c> client its own controllers use — and the API refuses a request on either
/// that does not carry the service credential (ADR-0055). Each road is pinned here, because a
/// key sent on one of them only is a BFF that can proxy and cannot log anyone in, or the reverse.
/// </summary>
public sealed class ServiceCredentialTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ServiceCredentialTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>What the API would receive, per request that reaches it.</summary>
    private sealed class Recorder : IForwarderHttpClientFactory
    {
        public List<string[]> Credentials { get; } = [];

        /// <summary>The session's bearer, as the destination would see it. Recorded because the
        /// credential is not the only secret a wrong destination would be handed.</summary>
        public List<string?> Authorization { get; } = [];

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Credentials.Add(
                request.Headers.TryGetValues(ServiceCredentialOptions.HeaderName, out var values)
                    ? [.. values]
                    : []);
            Authorization.Add(request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":null,"message":"ok"}""", Encoding.UTF8, "application/json"),
            };
        }

        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) =>
            new(new FakeBackendApiHandler(Respond));
    }

    private (WebApplicationFactory<Program> Host, Recorder Api) NewHost()
    {
        var recorder = new Recorder();
        var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(recorder));
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(recorder.Respond));
            }));
        return (host, recorder);
    }

    /// <summary>A proxied read behind a live session: the AuthLevelMiddlewareTests idiom.</summary>
    private static HttpRequestMessage ProxiedRead(WebApplicationFactory<Program> host)
    {
        var sessionId = host.Services.GetRequiredService<ISessionService>().CreateSession(
            "fake-jwt",
            DateTime.UtcNow.AddHours(1),
            "fake-refresh",
            new UserLoginInfo
            {
                Id = Guid.NewGuid(),
                AzureTag = "credential",
                Email = "credential@example.com",
                FirstName = "Service",
                LastName = "Credential",
                HasPin = true,
            });
        var cookieName = host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/accounts");
        request.Headers.Add("Cookie", $"{cookieName}={sessionId}");
        return request;
    }

    [Fact]
    public async Task TheProxy_SendsTheKey()
    {
        var (host, api) = NewHost();
        using var client = host.CreateClient();
        using var request = ProxiedRead(host);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the request has to reach the API to say anything");
        api.Credentials.Should().ContainSingle()
            .Which.Should().Equal(TestServiceCredential.Key);
    }

    [Fact]
    public async Task TheProxy_DropsAKeyTheBrowserSent_AndSendsItsOwn()
    {
        var (host, api) = NewHost();
        using var client = host.CreateClient();
        using var request = ProxiedRead(host);
        request.Headers.Add(ServiceCredentialOptions.HeaderName, "a-guess-from-the-browser");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the request has to reach the API to say anything");
        // One value, ours. Two would be refused by the API, so a browser could break its own
        // request by guessing; the browser's alone would be a browser choosing the credential.
        api.Credentials.Should().ContainSingle()
            .Which.Should().Equal(TestServiceCredential.Key);
    }

    [Fact]
    public async Task TheBffsOwnClient_SendsTheKey()
    {
        var (host, api) = NewHost();
        using var client = host.CreateClient();

        // Login is the BFF's own call to the API, not a proxied one.
        await client.PostAsJsonAsync(
            "/bff/auth/login", new { email = "someone@example.com", password = "Password123!" });

        api.Credentials.Should().NotBeEmpty();
        api.Credentials.Should().AllSatisfy(sent => sent.Should().Equal(TestServiceCredential.Key));
    }

    [Theory]
    [InlineData("https://api.internal", true)]
    [InlineData("https://localhost:7215", true)]
    [InlineData("http://localhost:5068", true)]
    [InlineData("http://127.0.0.1:5068", true)]
    [InlineData("http://[::1]:5068", true)]
    [InlineData("http://api.internal:5068", false)]
    [InlineData("http://10.0.0.7", false)]
    [InlineData("http://localhost.example.com", false)]
    [InlineData("ftp://localhost", false)]
    [InlineData("/relative", false)]
    [InlineData("", false)]
    public void TheKeyTravelsOverTlsOrToThisMachine(string destination, bool safe)
    {
        ServiceCredentialTransport
            .IsSafe(Uri.TryCreate(destination, UriKind.Absolute, out var uri) ? uri : null)
            .Should().Be(safe);
    }

    [Theory]
    [InlineData("BackendApi:BaseUrl")]
    [InlineData("ReverseProxy:Clusters:backend-api:Destinations:primary:Address")]
    public void TheHostValidatesBothRoadsAtStart(string road)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BackendApi:BaseUrl"] = "https://localhost:7215",
            ["ReverseProxy:Clusters:backend-api:Destinations:primary:Address"] = "https://localhost:7215",
        }).Build();
        ServiceCredentialTransport.UnsafeDestinations(configuration).Should().BeEmpty("the control: both roads are https");

        configuration[road] = "http://api.internal:5068";

        ServiceCredentialTransport.UnsafeDestinations(configuration)
            .Should().ContainSingle().Which.Should().Be($"{road} = http://api.internal:5068");

        // And the HOST carries the rule, not only the helper. Through the host a refusal to start
        // arrives as a disposed provider, so the wiring is read off a host that did start: its
        // configuration is pointed at the cleartext road afterwards, and the validators it ran at
        // start are asked again. The message is the one such a deployment would die with.
        using var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, layers) => layers.AddInMemoryCollection()));
        var validators = host.Services.GetServices<IValidateOptions<ServiceCredentialOptions>>().ToList();
        var usable = new ServiceCredentialOptions { BffKey = TestServiceCredential.Key };
        validators.Should().OnlyContain(
            validator => !validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, usable).Failed,
            "the control: as configured, the host's own roads are safe");

        host.Services.GetRequiredService<IConfiguration>()[road] = "http://api.internal:5068";

        validators.Select(validator => validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, usable))
            .Should().Contain(verdict => verdict.Failed
                && verdict.FailureMessage.Contains("The service credential would travel in clear"));
    }

    [Fact]
    public async Task TheProxy_ForwardsNothing_WhenAReloadPointsItAtACleartextDestination()
    {
        // Startup refuses such a destination, but YARP reloads its configuration while the host
        // runs. An in-memory layer added last outranks appsettings.json and survives Reload().
        var recorder = new Recorder();
        var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection());
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(recorder)));
        });
        using var client = host.CreateClient();
        using (var before = ProxiedRead(host))
        {
            await client.SendAsync(before);
        }

        // The control: while the destination is the configured https one, the request goes through
        // carrying both secrets. What changes below is the destination, and nothing else.
        recorder.Credentials.Should().ContainSingle().Which.Should().Equal(TestServiceCredential.Key);
        recorder.Authorization.Should().ContainSingle().Which.Should().Be("Bearer fake-jwt");

        var configuration = (IConfigurationRoot)host.Services.GetRequiredService<IConfiguration>();
        configuration["ReverseProxy:Clusters:backend-api:Destinations:primary:Address"] = "http://api.internal:5068";

        // Observed: the reload itself re-runs the options' validation and throws the startup
        // message out of the change callback. Every callback still runs, YARP's among them, so
        // the new destination is live all the same, and that is what the guard below is for.
        var reload = () => configuration.Reload();
        reload.Should().Throw<Exception>().WithMessage("*The service credential would travel in clear*");

        // The reload reaches YARP through a change token, so ask until it has.
        var reached = recorder.Credentials.Count;
        HttpResponseMessage? answer = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(100);
            using var after = ProxiedRead(host);
            answer?.Dispose();
            answer = await client.SendAsync(after);
            if (answer.StatusCode != HttpStatusCode.OK)
            {
                break;
            }
        }

        using (answer)
        {
            // FAIL CLOSED, not "send less". The first version of this guard withheld the
            // credential, logged, and fell through to the session block: measured on it, 50 of 50
            // requests reached http://api.internal:5068 and every one carried "Bearer fake-jwt".
            // So the assertion is on the REQUESTS, not on what they carried.
            recorder.Credentials.Should().HaveCount(reached, "no request may reach an unsafe destination");
            recorder.Authorization.Should().HaveCount(reached, "the session's bearer goes nowhere either");
            answer!.StatusCode.Should().Be(HttpStatusCode.BadGateway,
                "the caller is told the proxy could not forward, which is what YARP's 502 says");
        }
    }

    [Theory]
    // Development, loopback: the one place the development certificate lives.
    [InlineData(true, "https://localhost:7215", true)]
    [InlineData(true, "https://127.0.0.1:7215", true)]
    // Development, but NOT this machine: an unverifiable certificate there is whoever answered.
    [InlineData(true, "https://api.internal", false)]
    [InlineData(true, null, false)]
    // Outside Development the certificate is validated wherever the API is.
    [InlineData(false, "https://localhost:7215", false)]
    [InlineData(false, "https://api.internal", false)]
    public void TheBffsOwnClient_FollowsNoRedirect_AndTrustsNoCertificateOffThisMachine(
        bool development, string? destination, bool trustsAnyCertificate)
    {
        // .NET drops Authorization on a redirect that leaves the authority and keeps every other
        // header, so a followed redirect would carry the key wherever the 302 pointed.
        using var handler = ServiceCredentialTransport.CreateHandler(
            development, destination is null ? null : new Uri(destination));

        handler.AllowAutoRedirect.Should().BeFalse();
        (handler.ServerCertificateCustomValidationCallback is not null).Should().Be(trustsAnyCertificate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("thirty-one-characters-long-key!")]
    public void TheHostValidatesTheKeyAtStart(string unusable)
    {
        // Through the host the refusal arrives as a disposed provider (Program.cs catches, logs
        // and lets the process end), so the rule is read off the registration instead: the
        // validators this host runs at start, asked about a key it must not start with.
        var validators = _factory.Services.GetServices<IValidateOptions<ServiceCredentialOptions>>();

        var verdicts = validators
            .Select(validator => validator.Validate(
                Microsoft.Extensions.Options.Options.DefaultName, new ServiceCredentialOptions { BffKey = unusable }))
            .ToList();

        verdicts.Should().Contain(verdict => verdict.Failed
            && verdict.FailureMessage.Contains("ServiceCredential:BffKey must be configured"));

        // And the key it DID start with is the one bound from configuration.
        _factory.Services.GetRequiredService<IOptions<ServiceCredentialOptions>>().Value.BffKey
            .Should().Be(TestServiceCredential.Key);
    }
}
