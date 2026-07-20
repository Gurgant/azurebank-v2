using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Bff.Tests;

/// <summary>
/// Session-cookie posture (ADR-0018). The upstream API is faked at the named-client
/// primary handler, so a real login flows through the whole pipeline and the assertions
/// read the actual Set-Cookie header the browser would receive:
///  - it is a SESSION cookie — no Expires/Max-Age (lifetime is server-side);
///  - Development: unprefixed name, no Secure (the dev loop is http://localhost);
///  - Production: __Host- prefixed name, Secure — and the logout deletion carries the
///    same attributes, because a __Host- cookie is only evicted by a Secure Path=/ expiry.
/// </summary>
public class SessionCookieTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string LoginSuccessJson = """
        {
          "data": {
            "token": "fake-jwt-token",
            "expiresAt": "2030-01-01T00:00:00Z",
            "user": {
              "id": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
              "azureTag": "cookieuser",
              "email": "cookie@example.com",
              "firstName": "Cookie",
              "lastName": "User",
              "hasPin": false
            }
          },
          "message": "Login successful"
        }
        """;

    private readonly WebApplicationFactory<Program> _factory;

    public SessionCookieTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebApplicationFactory<Program> WithFakeBackend(string? environment = null)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            if (environment is not null)
            {
                builder.UseEnvironment(environment);
            }
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(() =>
                    new FakeBackendApiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(LoginSuccessJson, System.Text.Encoding.UTF8, "application/json")
                    }));
            });
        });
    }

    private static async Task<string> LoginAndGetSetCookie(HttpClient client)
    {
        // The BFF model-validates LoginRequest before any upstream call — the password
        // must satisfy the [Password] policy or the request 400s before the fake backend.
        var response = await client.PostAsJsonAsync(
            "/bff/auth/login", new { email = "cookie@example.com", password = "Password1!" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        response.Headers.TryGetValues("Set-Cookie", out var setCookies).Should().BeTrue(
            "a successful login must set the session cookie");
        return setCookies!.Single();
    }

    [Fact]
    public async Task Development_SetsUnprefixedSessionCookie_WithoutExpiresOrSecure()
    {
        var client = WithFakeBackend().CreateClient(); // WebApplicationFactory default env = Development

        var setCookie = await LoginAndGetSetCookie(client);

        setCookie.Should().StartWith(".AzureBank.Session=");
        setCookie.ToLowerInvariant().Should().Contain("httponly")
            .And.Contain("samesite=strict")
            .And.Contain("path=/")
            .And.NotContain("expires=", "a bank session cookie must not persist to disk")
            .And.NotContain("max-age", "a bank session cookie must not persist to disk")
            .And.NotContain("secure", "Safari refuses Secure cookies on http://localhost");
    }

    [Fact]
    public async Task Production_SetsHostPrefixedSecureSessionCookie_WithoutExpires()
    {
        var client = WithFakeBackend("Production").CreateClient();

        var setCookie = await LoginAndGetSetCookie(client);

        setCookie.Should().StartWith("__Host-AzureBank.Session=");
        setCookie.ToLowerInvariant().Should().Contain("secure", "__Host- requires Secure")
            .And.Contain("httponly")
            .And.Contain("samesite=strict")
            .And.Contain("path=/", "__Host- requires Path=/")
            .And.NotContain("domain=", "__Host- forbids Domain")
            .And.NotContain("expires=")
            .And.NotContain("max-age");
    }

    [Fact]
    public async Task Production_LogoutDeletion_CarriesTheSameSecureAttributes()
    {
        var factory = WithFakeBackend("Production");
        var client = factory.CreateClient();

        var setCookie = await LoginAndGetSetCookie(client);
        var sessionId = setCookie.Split(';')[0].Split('=', 2)[1];

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/bff/auth/logout");
        logout.Headers.Add("Cookie", $"__Host-AzureBank.Session={sessionId}");
        var response = await client.SendAsync(logout);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Set-Cookie", out var deletions).Should().BeTrue();
        var deletion = deletions!.Single().ToLowerInvariant();
        deletion.Should().StartWith("__host-azurebank.session=");
        // The browser only matches the stored __Host- cookie if the expiry is also
        // Secure with Path=/ — a bare Delete would leave the cookie alive.
        deletion.Should().Contain("expires=").And.Contain("secure").And.Contain("path=/");
    }
}
