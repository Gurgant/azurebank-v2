using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Bff.Tests;

/// <summary>
/// <see cref="TrustedProxyNetworkTests"/>' question on a real connection: the host listens on a
/// loopback socket, so the connection's address is the one the socket has (127.0.0.1) and not one
/// a test wrote, and a request is the bytes sent, among them bytes no HTTP client sends. The
/// network listed is loopback's, so this process stands where the proxy does: what it sends is
/// what a proxy would pass on, the caller's own header with the proxy's entry after it.
/// </summary>
/// <remarks>
/// <para>
/// As there, the address the host believed is read where it is used: the scripted API keeps the
/// <c>clientAddress</c> of every claim it is sent.
/// </para>
/// <para>
/// The port is the system's choice (<c>http://127.0.0.1:0</c>, set as configuration before the
/// host is built, for the reason <c>KestrelRequestSizeLimitTests</c> gives in the API's tests):
/// nothing here listens on a port another process may hold.
/// </para>
/// </remarks>
public sealed class ForwardedForOnARealConnectionTests : IDisposable
{
    private const string Loopback = "127.0.0.1";
    private const string LoopbackNetwork = "127.0.0.0/8";

    /// <summary>The caller, as the proxy appends it.</summary>
    private const string Caller = "203.0.113.9";

    /// <summary>An address a caller writes into the header itself, hoping to be taken for it.</summary>
    private const string Claimed = "198.51.100.200";

    private readonly List<OnASocket> _hosts = [];

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }
    }

    /// <summary>The BFF as the public demo, on a loopback socket, in front of a scripted API.</summary>
    private sealed class OnASocket(string? network) : WebApplicationFactory<Program>
    {
        public TrustedProxyNetworkTests.Api Api { get; } = new();

        public int Port => new Uri(Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseUrls($"http://{Loopback}:0");
            builder.UseSetting("Demo:Enabled", "true");
            if (network is not null)
            {
                builder.UseSetting("ForwardedHeaders:KnownIPNetworks:0", network);
            }

            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(Api.RespondAsync)));
        }
    }

    private OnASocket Start(string? network)
    {
        var host = new OnASocket(network);
        _hosts.Add(host);
        host.UseKestrel();
        host.StartServer();
        return host;
    }

    /// <summary>
    /// One claim, written to the socket byte for byte: the request line, what a claim needs, then
    /// each of <paramref name="headerLines"/> as it stands (one character a byte). Returns the
    /// status of the answer.
    /// </summary>
    private static async Task<int> ClaimAsync(int port, params string[] headerLines)
    {
        var head = new StringBuilder()
            .Append("POST /bff/auth/demo/claim HTTP/1.1\r\n")
            .Append($"Host: {Loopback}:{port}\r\n")
            .Append("Content-Type: application/json\r\n")
            .Append("Content-Length: 2\r\n")
            .Append("Connection: close\r\n");
        foreach (var line in headerLines)
        {
            head.Append(line).Append("\r\n");
        }

        head.Append("\r\n{}");

        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port);
        var stream = socket.GetStream();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await stream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), deadline.Token);
        using var answer = new MemoryStream();
        await stream.CopyToAsync(answer, deadline.Token);

        var text = Encoding.Latin1.GetString(answer.ToArray());
        text.Should().StartWith("HTTP/1.1 ", "the server answers every one of these");
        return int.Parse(text.AsSpan(9, 3), System.Globalization.CultureInfo.InvariantCulture);
    }

    // ── Behind the listed proxy ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a caller behind the proxy can write, by name: the header lines as the proxy passes
    /// them on. In every one the proxy's own entry, the caller's address, is the last. Named,
    /// because some hold characters a test's name should not.
    /// </summary>
    private static readonly Dictionary<string, string[]> WrittenByACaller = new()
    {
        ["nothing"] = [$"X-Forwarded-For: {Caller}"],
        ["an address"] = [$"X-Forwarded-For: {Claimed}, {Caller}"],
        // The two that were believed until 2026-10-06 (StrictForwardedFor says how): the first as
        // the address written, the second as its /64.
        ["an IPv4-mapped address, a zone and a quotation mark"] = [$"X-Forwarded-For: ::ffff:{Claimed}%\", {Caller}"],
        ["an IPv6 address, a zone and a quotation mark, no blank"] = [$"X-Forwarded-For: 2001:db8:dead:beef::1%\",{Caller}"],
        // Until that day this one was counted as the proxy itself.
        ["a quotation mark alone"] = [$"X-Forwarded-For: \", {Caller}"],
        ["a line of its own"] = [$"X-Forwarded-For: {Claimed}", $"X-Forwarded-For: {Caller}"],
        ["a line of its own that ends in a zone and a quotation mark"] =
            [$"X-Forwarded-For: ::ffff:{Claimed}%\"", $"X-Forwarded-For: {Caller}"],
        ["the name in lower case"] = [$"x-forwarded-for: {Claimed}, {Caller}"],
        ["a tab after the comma"] = [$"X-Forwarded-For: {Claimed},\t{Caller}"],
        // The server lets these two through (it refuses NUL, CR and LF: the theory below).
        ["the characters 0x01 and 0x7f"] = [$"X-Forwarded-For: {Claimed}\u0001\u007f, {Caller}"],
        ["30,000 characters of addresses"] =
            ["X-Forwarded-For: " + string.Join(", ", Enumerable.Repeat(Claimed, 1870)) + $", {Caller}"],
        ["the header the framework keeps the replaced address in"] =
            [$"X-Original-For: {Claimed}:1", $"X-Forwarded-For: {Caller}"],
        ["other headers that name an address"] =
            [$"X-Real-IP: {Claimed}", $"Forwarded: for={Claimed}", $"X_Forwarded_For: {Claimed}", $"X-Forwarded-For: {Caller}"],
    };

    public static TheoryData<string> WhatACallerWrites() => [.. WrittenByACaller.Keys];

    [Theory]
    [MemberData(nameof(WhatACallerWrites))]
    public async Task BehindTheListedProxy_TheCallerIsTheProxysEntry_WhateverItWroteItself(string what)
    {
        var host = Start(LoopbackNetwork);

        var status = await ClaimAsync(host.Port, WrittenByACaller[what]);

        status.Should().Be(200);
        host.Api.ClaimedFor.Should().Equal([Caller], what);
    }

    [Fact]
    public async Task BehindTheListedProxy_ACallerGetsNoNewBudgetByWritingAnAddress()
    {
        // The limiters count by the same address. The auth policy's ten a minute, as shipped:
        // twelve claims of one caller, each naming another address the way that was believed.
        var host = Start(LoopbackNetwork);

        var statuses = new List<int>();
        for (var i = 0; i < 12; i++)
        {
            statuses.Add(await ClaimAsync(host.Port, $"X-Forwarded-For: ::ffff:198.51.100.{i}%\", {Caller}"));
        }

        using (new AssertionScope())
        {
            statuses.Should().Equal(
                [.. Enumerable.Repeat(200, 10), 429, 429], "the eleventh claim of one caller in a minute is refused");
            host.Api.ClaimedFor.Should().Equal(
                Enumerable.Repeat(Caller, 10), "and every one that was made was made for that one caller");
        }
    }

    // ── What the server refuses before the app ───────────────────────────────────────────────────

    private static readonly Dictionary<string, (string[] Lines, int Status)> Refused = new()
    {
        // A line break is what a forged log line would need: none gets as far as the app.
        ["a line feed, and a log line after it"] =
            ([$"X-Forwarded-For: {Caller}\n[WRN] SecurityEvent RateLimitExceeded: forged"], 400),
        ["a carriage return"] = ([$"X-Forwarded-For: {Claimed}\r{Caller}"], 400),
        ["a folded line"] = ([$"X-Forwarded-For: {Claimed},\r\n {Caller}"], 400),
        ["a NUL"] = ([$"X-Forwarded-For: {Claimed}\0, {Caller}"], 400),
        ["a byte that is not ASCII"] = ([$"X-Forwarded-For: {Claimed}é, {Caller}"], 400),
        ["a blank before the colon"] = ([$"X-Forwarded-For : {Caller}"], 400),
        // More header lines than the server takes (100). An entry of 65,536 characters is over its
        // other limit (32,768 bytes of headers) and was answered 431 too, six times of six, but
        // not as a row here: the server closes while the rest of such a request is still
        // arriving, and the connection was then reset once before the answer was read. What the
        // APP does with an entry that long is a row of TrustedProxyNetworkTests, where no server
        // stands before it.
        ["200 lines"] =
            ([.. Enumerable.Range(0, 199).Select(i => $"X-Forwarded-For: 198.51.100.{i}"), $"X-Forwarded-For: {Caller}"], 431),
    };

    public static TheoryData<string> WhatTheServerRefuses() => [.. Refused.Keys];

    [Theory]
    [MemberData(nameof(WhatTheServerRefuses))]
    public async Task WhatTheServerRefuses_IsAnsweredAsAClientsError_AndNoClaimIsMade(string what)
    {
        var host = Start(LoopbackNetwork);

        var status = await ClaimAsync(host.Port, Refused[what].Lines);

        status.Should().Be(Refused[what].Status, what);
        host.Api.ClaimedFor.Should().BeEmpty(what);
    }

    // ── Not behind a listed proxy ────────────────────────────────────────────────────────────────

    [Theory]
    // CONTROL: nothing listed, the host as it ships. Green before a network could be listed.
    [InlineData(null, "an address")]
    [InlineData(null, "an IPv4-mapped address, a zone and a quotation mark")]
    [InlineData(null, "a line of its own")]
    // A network listed, and this connection is not from inside it.
    [InlineData("192.0.2.0/24", "an address")]
    [InlineData("192.0.2.0/24", "an IPv4-mapped address, a zone and a quotation mark")]
    [InlineData("192.0.2.0/24", "a line of its own")]
    public async Task FromAConnectionThatIsNoListedProxys_TheHeaderIsNotRead(string? network, string what)
    {
        var host = Start(network);

        var status = await ClaimAsync(host.Port, WrittenByACaller[what]);

        status.Should().Be(200);
        host.Api.ClaimedFor.Should().Equal([Loopback], what);
    }
}
