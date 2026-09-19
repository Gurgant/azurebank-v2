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

    /// <summary>What the API would receive under the credential's header name, per request.</summary>
    private sealed class Recorder : IForwarderHttpClientFactory
    {
        public List<string[]> Credentials { get; } = [];

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Credentials.Add(
                request.Headers.TryGetValues(ServiceCredentialOptions.HeaderName, out var values)
                    ? [.. values]
                    : []);
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
