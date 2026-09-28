using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AzureBank.Api.Middleware;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The token endpoints answer only the BFF's own client over loopback (06 §4.2, O2d): 404 to an
/// address that is not loopback, to a request without exactly one token-road marker, and to a null
/// address unless the test host says otherwise — the key and a live grant notwithstanding.
/// </summary>
/// <remarks>
/// <para>
/// Why it matters (06 §3): the grant no longer rotates because presenting one takes the grant, the
/// service key AND a socket on the API's loopback interface. Each refusal below is one of those legs.
/// </para>
/// <para>
/// Addresses come from <see cref="FakeRemoteAddressStartupFilter"/>; without its header a request has
/// the null address TestServer gives every request, which <see cref="CustomWebApplicationFactory"/>
/// accepts. The clients carry the key and one marker; a test that wants neither takes them off.
/// </para>
/// </remarks>
public class TokenRoadTests : IntegrationTestBase
{
    public TokenRoadTests(CustomWebApplicationFactory factory) : base(factory) { }

    /// <summary>The five token endpoints, each with a body it would otherwise accept.</summary>
    public static TheoryData<string> TokenEndpoints() =>
    [
        "/api/auth/login",
        "/api/auth/register",
        "/api/auth/refresh",
        "/api/auth/revoke",
        "/api/auth/logout",
    ];

    private async Task<(string Access, string Grant)> SignInAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var response = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"road_{unique}",
            Email = $"road{unique}@example.com",
            Password = TestUserPassword,
            FirstName = "Token",
            LastName = "Road"
        }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions))!.Data!.Token;
        return (token.AccessToken, token.RefreshToken!);
    }

    /// <summary>A POST with a body the endpoint would accept from the BFF, and a bearer for logout.</summary>
    private static HttpRequestMessage Post(string path, string grant, string access, string? from)
    {
        object body = path switch
        {
            "/api/auth/login" => new { email = "nobody@example.com", password = TestUserPassword },
            "/api/auth/register" => new
            {
                azureTag = "road_" + Guid.NewGuid().ToString("N")[..8],
                email = $"road-{Guid.NewGuid():N}@example.com",
                password = TestUserPassword,
                firstName = "Off",
                lastName = "Road",
            },
            "/api/auth/revoke" => new { refreshTokens = new[] { grant } },
            _ => new { refreshToken = grant },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        if (from is not null)
        {
            request.Headers.Add(FakeRemoteAddressStartupFilter.HeaderName, from);
        }

        return request;
    }

    [Theory]
    [MemberData(nameof(TokenEndpoints))]
    public async Task FromAnAddressThatIsNotLoopback_ATokenEndpointIs404_WithTheKeyTheMarkerAndALiveGrant(string path)
    {
        var (access, grant) = await SignInAsync();

        var response = await Client.SendAsync(Post(path, grant, access, from: "10.0.0.7"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "06 F1: only loopback reaches a token endpoint");

        // And nothing happened behind the 404: the grant still renews (from loopback).
        (await Client.SendAsync(Post("/api/auth/refresh", grant, access, from: "127.0.0.1")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheRefusal_LooksLikeAPathThatDoesNotExist()
    {
        // 404 and not 401 or 403, so a caller off the road learns only what an unknown path tells it.
        var (access, grant) = await SignInAsync();

        var refused = await Client.SendAsync(Post("/api/auth/refresh", grant, access, from: "10.0.0.7"));
        var unknown = await Client.SendAsync(Post("/api/auth/no-such-endpoint", grant, access, from: "10.0.0.7"));

        refused.StatusCode.Should().Be(unknown.StatusCode);
        refused.Content.Headers.ContentType?.MediaType.Should().Be(unknown.Content.Headers.ContentType?.MediaType);
        using var refusedBody = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        using var unknownBody = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        refusedBody.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(unknownBody.RootElement.EnumerateObject().Select(p => p.Name));
        refusedBody.RootElement.GetProperty("title").GetString()
            .Should().Be(unknownBody.RootElement.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]         // all of 127.0.0.0/8 is loopback
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]  // what a dual-mode socket reports for an IPv4 loopback client
    public async Task FromLoopback_WithTheMarker_ARenewalIsAnswered(string from)
    {
        var (access, grant) = await SignInAsync();

        var response = await Client.SendAsync(Post("/api/auth/refresh", grant, access, from));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task FromLoopback_WithoutExactlyOneMarker_ATokenEndpointIs404(int markers)
    {
        // Loopback alone is not the road: the BFF's proxy reaches the API over loopback too, with the
        // key on every browser request. Two markers are refused like two keys: picking one of them is
        // how a smuggled header gets believed (06 F15).
        var (access, grant) = await SignInAsync();
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Remove(ServiceCredentialOptions.TokenRoadHeaderName);
        var request = Post("/api/auth/refresh", grant, access, from: "127.0.0.1");
        for (var i = 0; i < markers; i++)
        {
            request.Headers.Add(ServiceCredentialOptions.TokenRoadHeaderName, ServiceCredentialOptions.TokenRoadMarker);
        }

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ANullAddress_IsRefused_UnlessTheHostSaysOtherwise()
    {
        // 06 F1: a null address is accepted only when code sets the option, and only the test host
        // does. Turned back off here, a request with no address — TestServer's — is refused.
        var (access, grant) = await SignInAsync();
        using var strict = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<TokenRoadOptions>(o => o.AcceptMissingRemoteAddress = false)));
        using var client = strict.CreateClient();

        var refused = await client.SendAsync(Post("/api/auth/refresh", grant, access, from: null));
        var fromLoopback = await client.SendAsync(Post("/api/auth/refresh", grant, access, from: "127.0.0.1"));

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        fromLoopback.StatusCode.Should().Be(HttpStatusCode.OK, "the same host answers the same grant over loopback");
        new TokenRoadOptions().AcceptMissingRemoteAddress.Should().BeFalse("never by default");
    }

    [Fact]
    public async Task WithoutTheKey_TheServiceCredentialRefusalComesFirst()
    {
        // Off the road AND without the key: the key's 401 answers, logged as the refusal it is, and
        // the road check never runs.
        var (access, grant) = await SignInAsync();
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Remove(ServiceCredentialOptions.HeaderName);

        var response = await client.SendAsync(Post("/api/auth/refresh", grant, access, from: "10.0.0.7"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.ServiceCredentialRequired);
    }

    [Fact]
    public async Task AnEndpointThatIsNotATokenEndpoint_IsNotOnTheRoadRule()
    {
        // The rule is the token endpoints', not the API's: the BFF's proxy reaches the rest.
        var (access, _) = await SignInAsync();
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Remove(ServiceCredentialOptions.TokenRoadHeaderName);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        request.Headers.Add(FakeRemoteAddressStartupFilter.HeaderName, "10.0.0.7");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
