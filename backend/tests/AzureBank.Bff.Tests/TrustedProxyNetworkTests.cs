using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace AzureBank.Bff.Tests;

/// <summary>
/// Which address the BFF takes for a caller's when it stands behind a proxy it was told to believe
/// by NETWORK (<c>ForwardedHeaders:KnownIPNetworks</c>, ADR-0013): the last entry of
/// <c>X-Forwarded-For</c>, and only on a connection that comes from inside a listed network. With
/// nothing listed the header is not read at all.
/// </summary>
/// <remarks>
/// <para>
/// The address is read where it is used. The demo's claim hands the API the caller's address as
/// the rate limiters count it (<see cref="ClientAddress"/>), so the scripted API here keeps the
/// <c>clientAddress</c> of every claim it was sent, and that text is the address the BFF believed.
/// The claim also spends the <c>auth</c> policy's budget, so a limit of 2 shows which callers
/// share one.
/// </para>
/// <para>
/// The test server gives a request no address: the connection's is the one the request's
/// <c>X-Test-Client-Ip</c> header names (<see cref="FakeRemoteIpStartupFilter"/>), set before the
/// app's own pipeline runs. In these tests it stands for the proxy's address.
/// </para>
/// </remarks>
public sealed class TrustedProxyNetworkTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string ClaimPath = "/bff/auth/demo/claim";

    /// <summary>The proxy's address on the connection: inside <see cref="Network"/>.</summary>
    private const string Proxy = "10.0.0.5";

    private const string Network = "10.0.0.0/8";
    private const string NetworksKey = "ForwardedHeaders:KnownIPNetworks:0";

    /// <summary>The caller, as the proxy appends it.</summary>
    private const string Caller = "203.0.113.9";

    /// <summary>An address a caller writes into the header itself, hoping to be taken for it.</summary>
    private const string Claimed = "198.51.100.200";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    public TrustedProxyNetworkTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }
    }

    /// <summary>
    /// The scripted API: the address each claim was made for, in arrival order. Internal, not
    /// private, since 2026-10-06: <see cref="ForwardedForOnARealConnectionTests"/> reads the same.
    /// </summary>
    internal sealed class Api
    {
        private int _claims;

        public ConcurrentQueue<string> ClaimedFor { get; } = new();

        public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/api/auth/demo/claim")
            {
                return Json("""{"data":null,"message":"ok"}""");
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            ClaimedFor.Enqueue(body.RootElement.GetProperty("clientAddress").GetString()!);
            var n = Interlocked.Increment(ref _claims);
            return Json($$"""
                {
                  "data": {
                    "token": {
                      "accessToken": "jwt-claim-{{n}}",
                      "refreshToken": "rt-claim-{{n}}",
                      "refreshTokenExpiresAt": "2030-01-01T01:00:00Z",
                      "sessionStamp": 7,
                      "expiresIn": 899,
                      "tokenType": "Bearer",
                      "expiresAt": "2030-01-01T00:15:00Z"
                    },
                    "user": {
                      "id": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
                      "azureTag": "john_k7m{{n}}",
                      "email": "demo-0123456789abcde{{n}}@azurebank.example",
                      "firstName": "John",
                      "lastName": "Smith",
                      "hasPin": true
                    },
                    "copy": {
                      "email": "demo-0123456789abcde{{n}}@azurebank.example",
                      "password": "Abcd-Efgh-Jkmn-Pqr2",
                      "pin": "123456",
                      "contacts": ["jane_k7m{{n}}", "mike_k7m{{n}}"],
                      "expiresAt": "2030-01-02T00:00:00Z"
                    }
                  },
                  "message": "Demo copy claimed"
                }
                """);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>A host that is the public demo, in front of a scripted API, with the settings given.</summary>
    private (WebApplicationFactory<Program> Host, Api Api) NewHost(
        (string Key, string Value)[] settings, Action<IServiceCollection>? configure = null)
    {
        var api = new Api();
        var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Demo:Enabled", "true");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(api.RespondAsync));
                services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>();
                configure?.Invoke(services);
            });
        });
        _hosts.Add(host);
        return (host, api);
    }

    /// <summary>A client that keeps no cookie: every claim is a visitor with no session.</summary>
    private static HttpClient Browser(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    /// <summary>
    /// One claim over a connection from <paramref name="connection"/> (none: a connection with no
    /// address), carrying each of <paramref name="forwardedFor"/> as an <c>X-Forwarded-For</c>
    /// header of its own.
    /// </summary>
    private static async Task<HttpStatusCode> ClaimAsync(
        HttpClient client, string? connection, params string[] forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ClaimPath)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        if (connection is not null)
        {
            request.Headers.Add(FakeRemoteIpStartupFilter.HeaderName, connection);
        }

        foreach (var value in forwardedFor)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", value).Should().BeTrue();
        }

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    // ── Nothing configured: today's behaviour ────────────────────────────────────────────────────

    // CONTROL: green before this change. RateLimiterTests.SpoofedXForwardedFor_DoesNotSplitThePartition
    // holds the same for the limiter; this reads the address itself, at the claim.
    [Fact]
    public async Task WithNothingListed_TheHeaderIsNotRead_AndTheCallerIsTheConnection()
    {
        var (host, api) = NewHost([]);
        using var client = Browser(host);

        var status = await ClaimAsync(client, Proxy, $"{Claimed}, {Caller}");

        status.Should().Be(HttpStatusCode.OK);
        api.ClaimedFor.Should().Equal(Proxy);
    }

    // CONTROL: green before this change. It is the state the deployment is in until the networks
    // are set: every caller behind one proxy is one client.
    [Fact]
    public async Task WithNothingListed_TwoCallersBehindOneProxy_ShareOneBudget()
    {
        var (host, api) = NewHost([("RateLimiting:AuthPermitLimit", "2")]);
        using var client = Browser(host);

        var first = await ClaimAsync(client, Proxy, "203.0.113.10");
        var second = await ClaimAsync(client, Proxy, "203.0.113.10");
        var another = await ClaimAsync(client, Proxy, "203.0.113.99");

        using (new AssertionScope())
        {
            new[] { first, second }.Should().AllBeEquivalentTo(HttpStatusCode.OK);
            another.Should().Be(HttpStatusCode.TooManyRequests,
                "with no proxy listed the second caller is the first one's client, and the limit here is 2");
            api.ClaimedFor.Should().Equal(Proxy, Proxy);
        }
    }

    // ── A network listed ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FromInsideTheNetwork_TheCallerIsTheLastEntry_AndOneItWroteItselfIsNotBelieved()
    {
        var (host, api) = NewHost([(NetworksKey, Network)]);
        using var client = Browser(host);

        // What the proxy sends on: the entry the caller wrote, then the caller's own address,
        // appended by the proxy.
        var status = await ClaimAsync(client, Proxy, $"{Claimed}, {Caller}");

        status.Should().Be(HttpStatusCode.OK);
        api.ClaimedFor.Should().Equal(
            [Caller], "ForwardLimit is 1: one entry is believed, the last, which the trusted proxy appended");
    }

    [Theory]
    [InlineData("192.0.2.44", "192.0.2.44")]
    // The addresses on either side of 10.0.0.0/8.
    [InlineData("9.255.255.255", "9.255.255.255")]
    [InlineData("11.0.0.0", "11.0.0.0")]
    // An IPv6 connection is in no IPv4 network.
    [InlineData("2001:db8:1:2::5", "2001:db8:1:2::/64")]
    // An IPv4 address seen through a dual-stack socket, outside the network like its plain form.
    [InlineData("::ffff:192.0.2.44", "192.0.2.44")]
    // This machine itself. The framework lists loopback by default (127.0.0.0/8 and ::1); here
    // only what was written is listed, so whatever else runs beside the BFF is not believed.
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "::/64")]
    public async Task FromOutsideTheNetwork_TheHeaderIsNotRead(string connection, string expected)
    {
        var (host, api) = NewHost([(NetworksKey, Network)]);
        using var client = Browser(host);

        var status = await ClaimAsync(client, connection, Caller);

        status.Should().Be(HttpStatusCode.OK);
        api.ClaimedFor.Should().Equal(
            [expected], "a caller that reaches the BFF without the proxy cannot name its own address");
    }

    [Theory]
    // The first and the last address of the network, and one in between.
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.5")]
    [InlineData("10.255.255.255")]
    // A dual-stack socket reports an IPv4 proxy in this form: it is inside the IPv4 network.
    [InlineData("::ffff:10.0.0.5")]
    public async Task AnAddressInsideTheNetwork_InItsPlainOrItsIPv4MappedForm_IsTheProxy(string connection)
    {
        var (host, api) = NewHost([(NetworksKey, Network)]);
        using var client = Browser(host);

        var status = await ClaimAsync(client, connection, Caller);

        status.Should().Be(HttpStatusCode.OK);
        api.ClaimedFor.Should().Equal(Caller);
    }

    [Fact]
    public async Task AnIPv6Network_IsListedTheSameWay()
    {
        var (host, api) = NewHost([(NetworksKey, "fd00::/8")]);
        using var client = Browser(host);

        var inside = await ClaimAsync(client, "fd00:1:2:3::5", Caller);
        var outside = await ClaimAsync(client, "fe00:1:2:3::5", Caller);

        using (new AssertionScope())
        {
            new[] { inside, outside }.Should().AllBeEquivalentTo(HttpStatusCode.OK);
            api.ClaimedFor.Should().Equal(Caller, "fe00:1:2:3::/64");
        }
    }

    [Fact]
    public async Task TwoCallersBehindTheProxy_AreTwoClients_ToTheLimiterAndToTheApi()
    {
        var (host, api) = NewHost([(NetworksKey, Network), ("RateLimiting:AuthPermitLimit", "2")]);
        using var client = Browser(host);

        var noisy = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            noisy.Add(await ClaimAsync(client, Proxy, "203.0.113.10"));
        }

        var another = await ClaimAsync(client, Proxy, "203.0.113.99");

        using (new AssertionScope())
        {
            noisy.Should().Equal(
                [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests],
                "the limit here is 2 for one client");
            another.Should().Be(HttpStatusCode.OK,
                "a second caller behind the same proxy is another client: before, it was refused with the first");
            api.ClaimedFor.Should().Equal(
                ["203.0.113.10", "203.0.113.10", "203.0.113.99"],
                "the API counts a day's copies by this address, so it has to differ as the limiter's key does");
        }
    }

    /// <summary>
    /// Headers of every shape a caller, or a proxy that is not the expected one, could send. The
    /// connection is the proxy's, inside the network, so the header IS read: each row says which
    /// address must come out of it. Where nothing in it can be believed, that is the proxy's own.
    /// </summary>
    public static TheoryData<string, string[], string> Headers() => new()
    {
        { "two headers: the last value of the last one", [Claimed, Caller], Caller },
        { "three headers", ["192.0.2.1", Claimed, Caller], Caller },
        { "a comma list with blanks", [$"{Claimed},192.0.2.1 ,  {Caller}"], Caller },
        { "a list, then a header of its own", [$"192.0.2.1, {Claimed}", Caller], Caller },
        { "an entry with a port", [$"{Claimed}, {Caller}:51234"], Caller },
        { "an IPv6 entry", [$"{Claimed}, 2001:db8:1:2::9"], "2001:db8:1:2::/64" },
        { "an IPv6 entry with a port", [$"{Claimed}, [2001:db8:1:2::9]:443"], "2001:db8:1:2::/64" },
        { "an IPv4-mapped entry", [$"{Claimed}, ::ffff:{Caller}"], Caller },
        { "the word unknown", ["unknown"], Proxy },
        { "the word unknown after an address", [$"{Claimed}, unknown"], Proxy },
        { "an obfuscated name", [$"{Claimed}, _hidden"], Proxy },
        { "an address with something after it", [$"{Claimed}, {Caller} x"], Proxy },
        { "a host name", [$"{Claimed}, proxy.example"], Proxy },
        { "an empty header", [""], Proxy },
        { "blanks and commas only", [" , ,"], Proxy },
        { "no header at all", [], Proxy },
        {
            "an over-long list: 4,000 entries a caller wrote, then the proxy's",
            [string.Join(", ", Enumerable.Range(0, 4000).Select(i => $"198.51.{i / 256}.{i % 256}")) + ", " + Caller],
            Caller
        },
        { "an over-long entry: 65,536 characters that are no address", [$"{Claimed}, {new string('a', 65536)}"], Proxy },

        // Added 2026-10-06, with StrictForwardedFor, which says what the framework's own reading
        // did with the first five: an address after a zone and a quotation mark was BELIEVED, as
        // written or as its /64, and a quotation mark alone made the caller the proxy itself.
        { "an IPv4-mapped address, a zone and a quotation mark, before the proxy's entry", [$"::ffff:{Claimed}%\", {Caller}"], Caller },
        { "the same with no blank after the comma", [$"::ffff:{Claimed}%\",{Caller}"], Caller },
        { "the same with an IPv6 address", [$"2001:db8:dead:beef::1%\", {Caller}"], Caller },
        { "the same after an entry of another proxy's", [$"192.0.2.1, ::ffff:{Claimed}%\", {Caller}"], Caller },
        { "a quotation mark alone before the proxy's entry", [$"\", {Caller}"], Caller },
        { "an address in quotation marks before the proxy's entry", [$"\"{Claimed}\", {Caller}"], Caller },
        { "a zone with no quotation mark: the comma ends the entry", [$"::ffff:{Claimed}%, {Caller}"], Caller },
        { "a tab after the comma", [$"{Claimed},\t{Caller}"], Caller },
        // A last entry that is not written as an address is none, however a lenient parser reads
        // it. No proxy writes its entry so; until that day each of these four WAS read, as the
        // address in the marks, as fe80::/64 twice, and as 203.0.113.9.
        { "a last entry in quotation marks", [$"{Claimed}, \"{Caller}\""], Proxy },
        { "a last entry with a numbered zone", [$"{Claimed}, fe80::1%3"], Proxy },
        { "a last entry with a named zone", [$"{Claimed}, fe80::1%eth0"], Proxy },
        { "a last entry in hexadecimal", [$"{Claimed}, 0xcb.0.113.9"], Proxy },
        { "an IPv4 address in brackets", [$"{Claimed}, [{Caller}]"], Proxy },
        { "a semicolon for the comma", [$"{Claimed}; {Caller}"], Proxy },
    };

    [Theory]
    [MemberData(nameof(Headers))]
    public async Task AHeaderOfAnyShape_IsAnswered_AndNoAddressTheCallerWroteIsBelieved(
        string what, string[] headers, string expected)
    {
        var (host, api) = NewHost([(NetworksKey, Network)]);
        using var client = Browser(host);

        var status = await ClaimAsync(client, Proxy, headers);

        status.Should().Be(HttpStatusCode.OK, what);
        api.ClaimedFor.Should().Equal([expected], what);
    }

    [Theory]
    [MemberData(nameof(Headers))]
    public async Task FromOutsideTheNetwork_AHeaderOfNoShapeIsRead(string what, string[] headers, string _)
    {
        // The rows above, sent over a connection that is not the proxy's: whatever each would be
        // read as from inside, here the caller is the connection.
        const string Outside = "192.0.2.44";
        var (host, api) = NewHost([(NetworksKey, Network)]);
        using var client = Browser(host);

        var status = await ClaimAsync(client, Outside, headers);

        status.Should().Be(HttpStatusCode.OK, what);
        api.ClaimedFor.Should().Equal([Outside], what);
    }

    [Fact]
    public async Task ACallerBehindTheProxy_GetsNoNewBudgetByWritingAnAddress()
    {
        // TwoCallersBehindTheProxy_AreTwoClients, turned round: ONE caller that names another
        // address in each request, the way that was believed until 2026-10-06, is one client.
        var (host, api) = NewHost([(NetworksKey, Network), ("RateLimiting:AuthPermitLimit", "2")]);
        using var client = Browser(host);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add(await ClaimAsync(client, Proxy, $"::ffff:198.51.100.{i}%\", {Caller}"));
        }

        using (new AssertionScope())
        {
            statuses.Should().Equal(
                [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests],
                "the limit here is 2 for one client, whatever it writes");
            api.ClaimedFor.Should().Equal(
                [Caller, Caller], "and the API counts the day's copies of the same one caller");
        }
    }

    [Fact]
    public async Task AConnectionWithNoAddress_IsNotAListedProxy()
    {
        // The framework's own middleware believes the header on a connection that has no address
        // at all (a Unix socket, a named pipe, this test server) once anything is listed. Such a
        // connection comes from no listed network, so here it is not believed.
        var (host, api) = NewHost([(NetworksKey, Network)]);
        using var client = Browser(host);

        var status = await ClaimAsync(client, connection: null, Caller);

        status.Should().Be(HttpStatusCode.OK);
        api.ClaimedFor.Should().Equal("unknown");
    }

    [Theory]
    // One hop believed: the last entry, even when it is itself an address of the network.
    [InlineData("1", $"{Claimed}, {Caller}, 10.0.0.7", "10.0.0.7")]
    // Two hops, both inside the network: the entry before the second proxy's.
    [InlineData("2", $"{Claimed}, {Caller}, 10.0.0.7", Caller)]
    // Two hops allowed, and the last entry is not a listed proxy: the walk stops there, and the
    // entry before it, which that stranger wrote, is not believed.
    [InlineData("2", $"{Claimed}, {Caller}", Caller)]
    // The same, the stranger's entry written the way that hid the comma after it until 2026-10-06.
    [InlineData("2", $"192.0.2.1, ::ffff:{Claimed}%\", {Caller}", Caller)]
    public async Task ForwardLimit_IsStillTheNumberOfHopsBelieved(string forwardLimit, string header, string expected)
    {
        var (host, api) = NewHost([(NetworksKey, Network), ("ForwardedHeaders:ForwardLimit", forwardLimit)]);
        using var client = Browser(host);

        var status = await ClaimAsync(client, Proxy, header);

        status.Should().Be(HttpStatusCode.OK);
        api.ClaimedFor.Should().Equal(expected);
    }

    // CONTROL: green before this change. No test ran the pipeline with a proxy listed by its exact
    // address; this holds that road across the change.
    [Theory]
    [InlineData("10.0.0.5", Caller)]
    [InlineData("10.0.0.6", "10.0.0.6")]
    public async Task AProxyListedByItsExactAddress_IsBelievedAsBefore_AndItsNeighbourIsNot(
        string connection, string expected)
    {
        var (host, api) = NewHost([("ForwardedHeaders:KnownProxies:0", "10.0.0.5")]);
        using var client = Browser(host);

        var status = await ClaimAsync(client, connection, $"{Claimed}, {Caller}");

        status.Should().Be(HttpStatusCode.OK);
        api.ClaimedFor.Should().Equal(expected);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.0.2.77")]
    public async Task AnExactAddressAndANetwork_AreListedTogether(string connection)
    {
        var (host, api) = NewHost(
            [("ForwardedHeaders:KnownProxies:0", "192.0.2.77"), (NetworksKey, Network)]);
        using var client = Browser(host);

        var status = await ClaimAsync(client, connection, $"{Claimed}, {Caller}");

        status.Should().Be(HttpStatusCode.OK);
        api.ClaimedFor.Should().Equal(Caller);
    }

    // ── Startup ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.0.0.0")]
    [InlineData("not-a-network")]
    public void AnEntryTheValidatorRefuses_StopsTheHost(string entry)
    {
        var refused = _factory.WithWebHostBuilder(builder => builder.UseSetting(NetworksKey, entry));
        _hosts.Add(refused);

        var exception = Record.Exception(() => refused.CreateClient());

        exception.Should().NotBeNull("an entry that cannot be believed must stop the host, not be skipped");

        // Any failure to start would satisfy the line above, and the test host does not show which
        // one it was: what it throws is an ObjectDisposedException. So the same key with a network:
        // that host starts, and what stopped the other one is the entry. The sentence that names
        // the entry is the validator's, and ProxyOptionsValidatorTests holds it.
        var accepted = _factory.WithWebHostBuilder(builder => builder.UseSetting(NetworksKey, Network));
        _hosts.Add(accepted);
        accepted.CreateClient();
        accepted.Services.GetRequiredService<IOptions<ProxyOptions>>().Value.KnownIPNetworks.Should().Equal(Network);
    }

    // ── What goes on to the API ──────────────────────────────────────────────────────────────────

    /// <summary>Stands in for the API on the proxy's road and keeps the headers it was sent.</summary>
    private sealed class RecordingApi : IForwarderHttpClientFactory
    {
        public List<Dictionary<string, string[]>> Requests { get; } = [];

        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) =>
            new(new FakeBackendApiHandler(Record));

        private HttpResponseMessage Record(HttpRequestMessage request)
        {
            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers.NonValidated)
            {
                headers[header.Key] = [.. header.Value];
            }

            Requests.Add(headers);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":null,"message":"proxied"}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnTheProxiedRoad_TheApiIsToldTheAddressTheBffBelieved(bool networkListed)
    {
        var recorded = new RecordingApi();
        var (host, _) = NewHost(
            networkListed ? [(NetworksKey, Network)] : [],
            services => services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(recorded)));
        var sessionId = host.Services.GetRequiredService<ISessionService>().CreateSession(
            "jwt-held-for-this-session",
            DateTime.UtcNow.AddHours(1),
            "fake-refresh",
            DateTime.UtcNow.AddMinutes(60),
            new UserLoginInfo
            {
                Id = Guid.NewGuid(),
                AzureTag = "behindproxy",
                Email = "behindproxy@example.com",
                FirstName = "Behind",
                LastName = "Proxy",
                HasPin = true,
            });
        var cookieName = host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        using var client = Browser(host);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/accounts");
        request.Headers.Add("Cookie", $"{cookieName}={sessionId}");
        request.Headers.Add(FakeRemoteIpStartupFilter.HeaderName, Proxy);
        request.Headers.Add("X-Forwarded-For", $"{Claimed}, {Caller}");
        request.Headers.Add("X-Real-IP", Claimed);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the request has to reach the API to say anything");
        var forwarded = recorded.Requests.Should().ContainSingle().Subject;
        using (new AssertionScope())
        {
            // The proxy writes this header itself, from the connection's address as the BFF has it
            // by then, and replaces whatever arrived under the name.
            forwarded.GetValueOrDefault("X-Forwarded-For").Should().Equal(networkListed ? Caller : Proxy);
            // No host reads this one: it goes on as the browser wrote it.
            forwarded.GetValueOrDefault("X-Real-IP").Should().Equal(Claimed);
            // The framework's middleware leaves the address it replaced under this name, with the
            // connection's port (the test server has none), and the proxy copies it on like any
            // other header. No host reads it either.
            string[] replaced = networkListed ? [$"{Proxy}:0"] : [];
            (forwarded.GetValueOrDefault("X-Original-For") ?? []).Should().Equal(replaced);
        }
    }
}
