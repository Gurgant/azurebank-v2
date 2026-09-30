using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AzureBank.Bff.Http;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace AzureBank.Bff.Tests;

/// <summary>
/// When the API does not answer within <c>BackendApi:TimeoutSeconds</c>, or cannot be reached, the
/// BFF gives the visitor the same JSON 503 the API gives when its database is down (ADR-0058):
/// <c>SERVICE_UNAVAILABLE</c>, <c>retryAfterSeconds</c>, <c>Retry-After</c>, <c>no-store</c>, and
/// the session kept. It never says whether anything was applied, because it cannot know.
/// </summary>
/// <remarks>
/// <para>
/// Both of the BFF's roads to the API. Its own <c>BackendApi</c> client, used by the six auth routes
/// it answers itself: before, a timeout escaped every one of them as an unhandled exception, a 500.
/// And the YARP proxy, whose activity timeout was its own default of 100 s: a call that got no
/// answer came back as an empty 504 after 100 s, and an API that refused the connection (a restart)
/// as an empty 502.
/// </para>
/// <para>
/// The timeout is one second here so the proofs are quick; production waits 55 s, which is above
/// the API's own 40-second deadline plus its cancellation and release (TimeoutChainTests).
/// </para>
/// </remarks>
public class BackendTimeoutTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string SessionEmail = "timeout@example.com";
    private const string GoodPassword = "Password1!";

    /// <summary>The <c>Retry-After</c> of an outage, shared with the API's 503 (ADR-0058).</summary>
    private const int OutageRetryAfterSeconds = 10;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;
    private readonly List<WebApplicationFactory<Program>> _derived = [];

    public BackendTimeoutTests(WebApplicationFactory<Program> factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    public void Dispose()
    {
        foreach (var derived in _derived)
        {
            derived.Dispose();
        }
    }

    private static readonly string LoginJson = $$"""
        {
          "data": {
            "token": {
              "accessToken": "jwt-timeout",
              "refreshToken": "rt-timeout",
              "expiresIn": 899,
              "tokenType": "Bearer",
              "expiresAt": "2030-01-01T00:00:00Z"
            },
            "user": {
              "id": "5f0f5c1e-7425-40de-944b-e07fc1f90ae7",
              "azureTag": "timeoutuser",
              "email": "{{SessionEmail}}",
              "firstName": "Time",
              "lastName": "Out",
              "hasPin": true
            }
          },
          "message": "Login successful"
        }
        """;

    /// <summary>How the scripted API behaves once the session is open.</summary>
    private enum ApiState
    {
        /// <summary>It answers every call with a successful sign-in.</summary>
        Answering,

        /// <summary>It answers nothing, and waits on the call's own token until the BFF gives up.</summary>
        Silent,

        /// <summary>It cannot be reached: the connection is refused.</summary>
        Refusing,

        /// <summary>It answers a success with no <c>data</c> in it, which the BFF cannot read.</summary>
        NoData,
    }

    /// <summary>The API, answering the sign-in that opens the session, then as <see cref="State"/> says.</summary>
    private sealed class ScriptedApi
    {
        public volatile ApiState State = ApiState.Answering;

        public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (State)
            {
                case ApiState.Silent:
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new UnreachableException();

                case ApiState.Refusing:
                    throw new HttpRequestException("Connection refused (test)");

                case ApiState.NoData:
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"data":null,"message":"ok"}""", Encoding.UTF8, "application/json"),
                    };

                default:
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(LoginJson, Encoding.UTF8, "application/json"),
                    };
            }
        }
    }

    /// <summary>The six routes the BFF answers itself with a call to the API.</summary>
    public static TheoryData<string> OwnRoutes => new()
    {
        "login", "register", "reauthenticate", "verify-pin", "set-pin", "rename",
    };

    private static HttpRequestMessage OwnRoute(string route) => route switch
    {
        "login" => new HttpRequestMessage(HttpMethod.Post, "/bff/auth/login")
        {
            Content = JsonContent.Create(new { email = SessionEmail, password = GoodPassword }),
        },
        "register" => new HttpRequestMessage(HttpMethod.Post, "/bff/auth/register")
        {
            Content = JsonContent.Create(new
            {
                azureTag = "timeout_new",
                email = "timeout.new@example.com",
                password = GoodPassword,
                firstName = "Time",
                lastName = "Out",
            }),
        },
        "reauthenticate" => new HttpRequestMessage(HttpMethod.Post, "/bff/auth/reauthenticate")
        {
            Content = JsonContent.Create(new { password = GoodPassword }),
        },
        "verify-pin" => new HttpRequestMessage(HttpMethod.Post, "/bff/auth/verify-pin")
        {
            Content = JsonContent.Create(new { pin = "123456" }),
        },
        "set-pin" => new HttpRequestMessage(HttpMethod.Post, "/bff/auth/set-pin")
        {
            Content = JsonContent.Create(new { pin = "123456", password = GoodPassword }),
        },
        "rename" => new HttpRequestMessage(HttpMethod.Patch, "/bff/auth/azuretag")
        {
            Content = JsonContent.Create(new { azureTag = "timeout_renamed" }),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "not one of the six"),
    };

    private WebApplicationFactory<Program> Host(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> api,
        IForwarderHttpClientFactory? forwarder = null)
    {
        var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("BackendApi:TimeoutSeconds", "1");
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(api));
                if (forwarder is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton(forwarder));
                }
            });
        });
        _derived.Add(host);
        return host;
    }

    private static async Task<string> SignInAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/bff/auth/login", new { email = SessionEmail, password = GoodPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the API answers the sign-in that opens the session");
        return response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
    }

    [Theory]
    [MemberData(nameof(OwnRoutes))]
    public async Task AnApiThatDoesNotAnswerInTime_GivesTheJson503_AndKeepsTheSession(string route)
    {
        await OwnRouteDuringAsync(route, ApiState.Silent);
    }

    [Theory]
    [MemberData(nameof(OwnRoutes))]
    public async Task AnApiThatCannotBeReached_GivesTheJson503_AndKeepsTheSession(string route)
    {
        // Before: a bare Problem(503), with no errorCode and no retryAfterSeconds, which the
        // SPA could not tell from any other failure.
        await OwnRouteDuringAsync(route, ApiState.Refusing);
    }

    /// <summary>The five of the six routes that read the API's success; set-pin needs only its status.</summary>
    public static TheoryData<string> RoutesThatReadTheSuccess => new()
    {
        "login", "register", "reauthenticate", "verify-pin", "rename",
    };

    [Theory]
    [MemberData(nameof(RoutesThatReadTheSuccess))]
    public async Task ASuccessWithNoDataInIt_GivesTheJson503_AndKeepsTheSession(string route)
    {
        // Until this change: a null reference escaped sign-in, registration and re-authentication as
        // a 500, verify-pin answered "Invalid PIN" (a verdict the API never gave), and rename
        // forwarded the unreadable body with its 200.
        await OwnRouteDuringAsync(route, ApiState.NoData);
    }

    private async Task OwnRouteDuringAsync(string route, ApiState state)
    {
        var api = new ScriptedApi();
        var host = Host(api.RespondAsync);
        var client = host.CreateClient();

        // "Name=value" as it went out in Set-Cookie; carried by hand, CreateClient() keeps no jar.
        var cookie = await SignInAsync(client);
        var sessionId = cookie.Split('=', 2)[1];
        api.State = state;

        using var request = OwnRoute(route);
        request.Headers.Add("Cookie", cookie);
        var clock = Stopwatch.StartNew();
        var response = await client.SendAsync(request);
        clock.Stop();

        _output.WriteLine($"{route} ({state}): {(int)response.StatusCode} in {clock.ElapsedMilliseconds} ms: "
                          + await response.Content.ReadAsStringAsync());

        await AssertOutageAsync(response);
        clock.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(5), "the BFF's own timeout is one second here, and it answers when it fires");
        host.Services.GetRequiredService<ISessionService>().GetSession(sessionId).Should().NotBeNull(
            "a 503 is the service failing, not the session: the visitor stays signed in");
    }

    /// <summary>
    /// YARP's forwarder, answering with <paramref name="send"/>: a stand-in for the API behind the
    /// proxy, which the proxy reaches through its own client rather than <c>BackendApi</c>.
    /// </summary>
    private sealed class Forwarder(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : IForwarderHttpClientFactory
    {
        public Forwarder(Func<CancellationToken, Task<HttpResponseMessage>> send)
            : this((_, cancellationToken) => send(cancellationToken))
        {
        }

        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(new Handler(send));

        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        }
    }

    /// <summary>A connection to the API that breaks as soon as anything is written to it.</summary>
    private sealed class BrokenConnection : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("Connection reset by peer (test)");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Connection reset by peer (test)"));
    }

    private async Task<(HttpResponseMessage Response, TimeSpan Elapsed)> ProxiedAsync(
        IForwarderHttpClientFactory forwarder, HttpContent? body = null)
    {
        var host = Host(new ScriptedApi().RespondAsync, forwarder);
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var cookieName = host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        var sessionId = sessions.CreateSession(
            "jwt-proxied",
            DateTime.UtcNow.AddMinutes(15),
            "rt-proxied",
            DateTime.UtcNow.AddMinutes(60),
            new UserLoginInfo
            {
                Id = Guid.NewGuid(),
                AzureTag = "proxied",
                Email = "proxied@example.com",
                FirstName = "Pro",
                LastName = "Xied",
                HasPin = true,
            });

        var client = host.CreateClient();
        // Past YARP's own 100 s default, so the old behaviour is observed rather than cut short by
        // the test client's own 100 s.
        client.Timeout = TimeSpan.FromSeconds(150);

        using var request = body is null
            ? new HttpRequestMessage(HttpMethod.Get, "/api/accounts")
            : new HttpRequestMessage(HttpMethod.Post, "/api/transfers") { Content = body };
        request.Headers.Add("Cookie", $"{cookieName}={sessionId}");
        var clock = Stopwatch.StartNew();
        var response = await client.SendAsync(request);
        clock.Stop();

        _output.WriteLine($"proxied: {(int)response.StatusCode} in {clock.ElapsedMilliseconds} ms, "
                          + $"content-type '{response.Content.Headers.ContentType}': "
                          + await response.Content.ReadAsStringAsync());
        sessions.GetSession(sessionId).Should().NotBeNull("a 503 keeps the session");
        return (response, clock.Elapsed);
    }

    [Fact]
    public async Task AProxiedCallTheApiDoesNotAnswer_GivesTheJson503_AtTheBffsTimeout()
    {
        var (response, elapsed) = await ProxiedAsync(new Forwarder(async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }));

        await AssertOutageAsync(response);
        elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(5),
            "BackendApi:TimeoutSeconds is YARP's activity timeout too, one second here, not YARP's 100 s");
    }

    [Fact]
    public async Task AProxiedCallTheApiRefuses_GivesTheJson503()
    {
        // ForwarderError.Request: the connection was refused or dropped, as while the API restarts.
        var (response, elapsed) = await ProxiedAsync(new Forwarder(_ =>
            throw new HttpRequestException("Connection refused (test)")));

        await AssertOutageAsync(response);
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AProxiedWriteWhoseConnectionBreaksMidBody_GivesTheJson503()
    {
        // ForwarderError.RequestBodyDestination: the API's connection failed while the request's
        // body was being sent to it, as when the API goes down mid-request. YARP answered an empty
        // 502, or an empty 504 had its timeout fired by then.
        var (response, elapsed) = await ProxiedAsync(
            new Forwarder(async (request, cancellationToken) =>
            {
                await request.Content!.CopyToAsync(new BrokenConnection(), cancellationToken);
                throw new UnreachableException();
            }),
            new StringContent("""{"amount":1}""", Encoding.UTF8, "application/json"));

        await AssertOutageAsync(response);
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void TheShippedProxyCluster_WaitsAsLongAsTheBffsOwnClient()
    {
        // The two roads to the API wait one length of time: before, the proxy waited YARP's 100 s
        // whatever BackendApi:TimeoutSeconds said.
        var options = _factory.Services.GetRequiredService<IOptions<BackendApiOptions>>().Value;
        var proxy = _factory.Services.GetRequiredService<IProxyStateLookup>();

        proxy.TryGetCluster("backend-api", out var cluster).Should().BeTrue("the shipped configuration has it");
        (cluster!.Model.Config.HttpRequest?.ActivityTimeout).Should().Be(
            TimeSpan.FromSeconds(options.TimeoutSeconds), "BackendApi:TimeoutSeconds drives the proxy too");
    }

    [Fact]
    public async Task AClusterThatSetsItsOwnActivityTimeout_KeepsIt_AndEveryOtherGetsTheBffs()
    {
        var filter = new BackendTimeoutConfigFilter(Microsoft.Extensions.Options.Options.Create(
            new BackendApiOptions { TimeoutSeconds = 7 }));

        var plain = await filter.ConfigureClusterAsync(
            new ClusterConfig { ClusterId = "plain" }, CancellationToken.None);
        (plain.HttpRequest?.ActivityTimeout).Should().Be(TimeSpan.FromSeconds(7), "a cluster that names none");

        var tuned = await filter.ConfigureClusterAsync(
            new ClusterConfig
            {
                ClusterId = "tuned",
                HttpRequest = new ForwarderRequestConfig { Version = HttpVersion.Version20 },
            },
            CancellationToken.None);
        (tuned.HttpRequest?.ActivityTimeout).Should().Be(TimeSpan.FromSeconds(7), "a cluster that names other settings");
        (tuned.HttpRequest?.Version).Should().Be(HttpVersion.Version20, "and keeps those settings");

        var own = await filter.ConfigureClusterAsync(
            new ClusterConfig
            {
                ClusterId = "own",
                HttpRequest = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(300) },
            },
            CancellationToken.None);
        (own.HttpRequest?.ActivityTimeout).Should().Be(
            TimeSpan.FromSeconds(300), "an operator who wrote a value for one cluster meant it");
    }

    /// <summary>
    /// The BFF's outage 503: the API's shape, and no <c>applied</c>, which only the API can know.
    /// </summary>
    internal static async Task AssertOutageAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, $"the body was: {text}");
        response.Headers.RetryAfter.Should().NotBeNull("an outage 503 says when to come back");
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(OutageRetryAfterSeconds));
        response.Headers.CacheControl.Should().NotBeNull("an outage answer must never be cached");
        response.Headers.CacheControl!.NoStore.Should().BeTrue("an outage answer must never be cached");

        var body = JsonSerializer.Deserialize<JsonElement>(text);
        body.GetProperty("status").GetInt32().Should().Be(503);
        body.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.ServiceUnavailable);
        body.GetProperty("retryAfterSeconds").GetInt32().Should().Be(
            OutageRetryAfterSeconds, "the SPA reads the body; the header does not always reach it");
        body.TryGetProperty("applied", out _).Should().BeFalse(
            "the BFF cannot know whether the API applied anything, so it never says");
    }
}
