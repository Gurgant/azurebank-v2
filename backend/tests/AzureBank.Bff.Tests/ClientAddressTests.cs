using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The one text a client is known by (<see cref="ClientAddress"/>): an IPv4 address in full, an
/// IPv6 address as its /64, and <c>unknown</c> for a connection with no address. The rate limiters
/// partition on it (ADR-0013).
/// </summary>
public class ClientAddressTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ClientAddressTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    // An IPv4 client seen through a dual-stack socket is the same client.
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    // An IPv6 end site is handed a whole /64: every address in it is one client.
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    [InlineData("::1", "::/64")]
    [InlineData(null, "unknown")]
    public void TheKey_IsAnIPv4AddressInFull_AnIPv6AddressAsItsSlash64_AndUnknownForNone(string? remote, string expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);

        ClientAddress.Of(context).Should().Be(expected);
    }

    [Fact]
    public void TheLongestKey_FitsTheClientAddressTheApiTakes()
    {
        // The claim sends this text to the API, which takes at most 64 characters.
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff");

        var key = ClientAddress.Of(context);

        key.Should().Be("ffff:ffff:ffff:ffff::/64");
        key.Length.Should().BeLessThanOrEqualTo(AzureBank.Shared.DTOs.Auth.DemoClaimRequest.MaxClientAddressLength);
    }

    // CONTROL: green before this change. The limiters keyed an IPv6 client on its /64 while the
    // rule was a local function of Program.cs, and no test said so: this pins it across the move.
    [Fact]
    public async Task TheAuthLimiter_CountsEveryAddressOfOneIPv6Slash64AsOneClient()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:AuthPermitLimit", 2.ToString(CultureInfo.InvariantCulture));
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>());
        }).CreateClient();

        async Task<HttpStatusCode> SignInFrom(string address)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/auth/login")
            {
                Content = JsonContent.Create(new { azureTag = "probe", password = "x" }),
            };
            request.Headers.Add(FakeRemoteIpStartupFilter.HeaderName, address);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        var first = await SignInFrom("2001:db8:1:2::1");
        var second = await SignInFrom("2001:db8:1:2::2");
        var third = await SignInFrom("2001:db8:1:2:ffff:ffff:ffff:ffff");
        var fromAnotherSite = await SignInFrom("2001:db8:1:3::1");

        first.Should().NotBe(HttpStatusCode.TooManyRequests);
        second.Should().NotBe(HttpStatusCode.TooManyRequests);
        third.Should().Be(HttpStatusCode.TooManyRequests,
            "three addresses of one /64 are one client, and the limit here is 2");
        fromAnotherSite.Should().NotBe(HttpStatusCode.TooManyRequests, "another /64 is another client");
    }
}
