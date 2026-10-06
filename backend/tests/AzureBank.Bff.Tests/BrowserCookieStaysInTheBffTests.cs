using System.Net;
using System.Text;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Implementations;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Options;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The browser's <c>Cookie</c> header stops at the BFF. The session id in it is how the BFF finds
/// the tokens it holds server-side; the API is called with the bearer the BFF attaches and reads no
/// cookie, so a proxied request carries none: not the session's, and not any other cookie the
/// browser sent with it.
/// </summary>
/// <remarks>
/// The client YARP forwards with is replaced by a recorder that keeps every header of the request
/// as the proxy hands it over, so "no Cookie header left" is read from the request itself. The
/// proxy's own transforms still run: only the last step, the sending, is the recorder's.
/// </remarks>
public sealed class BrowserCookieStaysInTheBffTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    public BrowserCookieStaysInTheBffTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }
    }

    /// <summary>One request as the proxy handed it over to be sent to the API.</summary>
    private sealed record Forwarded(
        string Method, string PathAndQuery, IReadOnlyDictionary<string, string[]> Headers)
    {
        /// <summary>Every value that arrived under the name, or none.</summary>
        public string[] Values(string name) => Headers.TryGetValue(name, out var values) ? values : [];
    }

    /// <summary>Stands in for the API on the proxy's road and keeps what it was sent.</summary>
    private sealed class RecordingApi : IForwarderHttpClientFactory
    {
        public List<Forwarded> Requests { get; } = [];

        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) =>
            new(new FakeBackendApiHandler(Record));

        private HttpResponseMessage Record(HttpRequestMessage request)
        {
            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers.NonValidated)
            {
                headers[header.Key] = [.. header.Value];
            }

            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers.NonValidated)
                {
                    headers[header.Key] = [.. header.Value];
                }
            }

            Requests.Add(new Forwarded(request.Method.Method, request.RequestUri!.PathAndQuery, headers));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":null,"message":"proxied"}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// Ends the session, then asks the real refresher for its token: the state of a request that
    /// passed the session gate a moment before "Esci" was pressed in another tab. The refresher
    /// answers that the session ended, and the request is forwarded with no bearer.
    /// </summary>
    private sealed class SessionEndsBeforeTheToken(TokenRefresher real, ISessionService sessions) : ITokenRefresher
    {
        public Task<AccessTokenResult> GetAccessTokenAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            sessions.EndSession(sessionId);
            return real.GetAccessTokenAsync(sessionId, cancellationToken);
        }
    }

    private (WebApplicationFactory<Program> Host, RecordingApi Api) NewHost(
        Action<IServiceCollection>? configure = null)
    {
        var api = new RecordingApi();
        var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(api));

                // The BFF's OWN client, not the proxy's road recorded above: a host that stops
                // revokes the grants of the sessions it held, and would otherwise dial the default
                // API address and wait to be refused.
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(() =>
                    new FakeBackendApiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"message":"ok"}""", Encoding.UTF8, "application/json"),
                    }));

                configure?.Invoke(services);
            }));
        _hosts.Add(host);
        return (host, api);
    }

    /// <summary>A live session holding <paramref name="accessToken"/>; returns its cookie pair.</summary>
    private static string SignIn(WebApplicationFactory<Program> host, string accessToken)
    {
        var sessionId = host.Services.GetRequiredService<ISessionService>().CreateSession(
            accessToken,
            DateTime.UtcNow.AddHours(1),
            "fake-refresh",
            DateTime.UtcNow.AddMinutes(60),
            new UserLoginInfo
            {
                Id = Guid.NewGuid(),
                AzureTag = "cookiejar",
                Email = "cookiejar@example.com",
                FirstName = "Cookie",
                LastName = "Jar",
                HasPin = true,
            });
        var cookieName = host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        return $"{cookieName}={sessionId}";
    }

    private static HttpRequestMessage Proxied(HttpMethod method, string path, params string[] cookieHeaders)
    {
        var request = new HttpRequestMessage(method, path);
        foreach (var cookieHeader in cookieHeaders)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader).Should().BeTrue();
        }

        return request;
    }

    [Theory]
    // The two routes a signed-in browser's request is proxied on: the catch-all, and the handle
    // lookup, which is a route of its own because it carries its own rate limit.
    [InlineData("/api/accounts")]
    [InlineData("/api/users/someone")]
    public async Task WithALiveSession_TheApiGetsTheBearer_AndNoCookieHeader(string path)
    {
        var (host, api) = NewHost();
        var session = SignIn(host, "jwt-held-for-this-session");
        using var client = host.CreateClient();

        var response = await client.SendAsync(Proxied(HttpMethod.Get, path, session));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the request has to reach the API to say anything");
        var forwarded = api.Requests.Should().ContainSingle().Subject;
        forwarded.PathAndQuery.Should().Be(path);
        using (new AssertionScope())
        {
            forwarded.Values("Authorization").Should().Equal(
                ["Bearer jwt-held-for-this-session"], "the session resolved, and its token is what the API is called with");
            forwarded.Values("Cookie").Should().BeEmpty(
                "the session id is the BFF's own secret, and the API reads no cookie");
        }
    }

    [Theory]
    // Where the session's cookie sits among the others, and the three sent as one header or as two.
    [InlineData("theme=dark; SESSION; _ga=GA1.2.345.678")]
    [InlineData("SESSION; theme=dark; _ga=GA1.2.345.678")]
    [InlineData("theme=dark; _ga=GA1.2.345.678; SESSION")]
    [InlineData("theme=dark; _ga=GA1.2.345.678|SESSION")]
    public async Task WithTwoOtherCookiesBesideTheSessions_TheApiGetsTheBearer_AndNoneOfTheThree(string layout)
    {
        var (host, api) = NewHost();
        var session = SignIn(host, "jwt-held-for-this-session");
        using var client = host.CreateClient();

        var cookieHeaders = layout.Replace("SESSION", session, StringComparison.Ordinal).Split('|');
        var response = await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", cookieHeaders));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the request has to reach the API to say anything");
        var forwarded = api.Requests.Should().ContainSingle().Subject;
        using (new AssertionScope())
        {
            forwarded.Values("Authorization").Should().Equal(
                ["Bearer jwt-held-for-this-session"], "the session's cookie was read among the others");
            forwarded.Values("Cookie").Should().BeEmpty(
                "no cookie of the browser's is the API's to read, the session's or any other");
        }
    }

    [Fact]
    public async Task EachOfTwoSessions_IsForwardedWithItsOwnToken_AndNoCookie()
    {
        // The BFF goes on reading the cookie it no longer forwards: which token a request is sent
        // with is still decided by the session id the browser presented.
        var (host, api) = NewHost();
        var first = SignIn(host, "jwt-of-the-first-session");
        var second = SignIn(host, "jwt-of-the-second-session");
        using var client = host.CreateClient();

        var firstResponse = await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", first));
        var secondResponse = await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", second));

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        api.Requests.Should().HaveCount(2, "each request is forwarded");
        using (new AssertionScope())
        {
            api.Requests[0].Values("Authorization").Should().Equal("Bearer jwt-of-the-first-session");
            api.Requests[1].Values("Authorization").Should().Equal("Bearer jwt-of-the-second-session");
            api.Requests[0].Values("Cookie").Should().BeEmpty();
            api.Requests[1].Values("Cookie").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task WhenTheSessionEndsOnTheWayToTheProxy_TheRequestGoesWithNoBearer_AndNoCookieEither()
    {
        // The one forwarded request with no session behind it: it passed the session gate, and the
        // session ended before the proxy asked for its token. The cookie is dropped whether or not a
        // session resolves, so this request carries nothing of the browser's session at all.
        var (host, api) = NewHost(services =>
        {
            services.AddSingleton<TokenRefresher>();
            services.Replace(ServiceDescriptor.Singleton<ITokenRefresher, SessionEndsBeforeTheToken>());
        });
        var session = SignIn(host, "jwt-held-for-this-session");
        using var client = host.CreateClient();

        await client.SendAsync(Proxied(HttpMethod.Get, "/api/accounts", $"theme=dark; {session}"));

        var forwarded = api.Requests.Should().ContainSingle(
            "the gate saw a live session, so the request is forwarded").Subject;
        using (new AssertionScope())
        {
            forwarded.Values("Authorization").Should().BeEmpty("the session ended: there is no token to send");
            forwarded.Values("Cookie").Should().BeEmpty(
                "the cookie is dropped on every proxied request, not only on one a session answers for");
        }
    }

    // CONTROL: this passes with the cookie forwarded and with it dropped. Dropping it changes
    // nothing else the API is called with: the method, the path and the query as the browser sent
    // them, the session's bearer, this host's service key, the body's type, and the two headers a
    // money request rides on.
    [Fact]
    public async Task TheRestOfTheForwardedRequestIsWhatItWas()
    {
        var (host, api) = NewHost();
        var session = SignIn(host, "jwt-held-for-this-session");
        using var client = host.CreateClient();

        const string read = "/api/accounts/01a01234-0000-7000-8000-000000000000/transactions?page=2&pageSize=5";
        await client.SendAsync(Proxied(HttpMethod.Get, read, $"theme=dark; {session}"));

        var write = Proxied(HttpMethod.Post, "/api/transfers", $"theme=dark; {session}");
        write.Headers.Add(IdempotencyConstants.HeaderName, "0198c0de-0000-7000-8000-00000000cafe");
        write.Headers.Add(StepUpConstants.HeaderName, "0198c0de-0000-7000-8000-00000000beef");
        write.Content = new StringContent("""{"amount":1}""", Encoding.UTF8, "application/json");
        await client.SendAsync(write);

        api.Requests.Should().HaveCount(2, "both requests are forwarded");
        using (new AssertionScope())
        {
            api.Requests[0].Method.Should().Be("GET");
            api.Requests[0].PathAndQuery.Should().Be(read);
            api.Requests[1].Method.Should().Be("POST");
            api.Requests[1].PathAndQuery.Should().Be("/api/transfers");
            api.Requests[1].Values(IdempotencyConstants.HeaderName)
                .Should().Equal("0198c0de-0000-7000-8000-00000000cafe");
            api.Requests[1].Values(StepUpConstants.HeaderName)
                .Should().Equal("0198c0de-0000-7000-8000-00000000beef");
            api.Requests[1].Values("Content-Type").Should().Equal("application/json; charset=utf-8");
            foreach (var forwarded in api.Requests)
            {
                forwarded.Values("Authorization").Should().Equal("Bearer jwt-held-for-this-session");
                forwarded.Values(ServiceCredentialOptions.HeaderName).Should().Equal(TestServiceCredential.Key);
            }
        }
    }
}
