using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Bff.Tests;

/// <summary>
/// A rename has to reach the CACHED session, not just the database (ADR-0015's named follow-up).
///
/// <para>
/// Measured on the running stack before this endpoint existed, which is what turned an accepted
/// residual into a defect: <c>PATCH /api/users/me/azuretag</c> through the proxy returned 200 and
/// the database held the new handle, yet <c>GET /bff/auth/me</c> answered the OLD one for the life
/// of the session — and kept answering it when re-fetched. A fresh login showed the new handle, so
/// only the cache was wrong.
/// </para>
/// <para>
/// That is why the mitigation ADR-0015 recorded — "the frontend should re-fetch /me after a rename"
/// — could not work, and why <c>apiSlice.ts</c> claiming the refetch "picks up the new tag (same
/// pattern as setPin)" was false: set-pin is BFF-owned and writes the cache back, the proxied rename
/// ran no BFF code at all.
/// </para>
/// </summary>
public class AzureTagRenameTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string SessionEmail = "cookieuser@azurebank.dev";
    private const string GoodPassword = "Password123!";
    private const string OriginalTag = "cookieuser";
    private const string RenamedTag = "cookieuser_renamed";

    private static string LoginSuccessJson() => $$"""
        {
          "data": {
            "token": "jwt-1",
            "expiresAt": "2030-01-01T00:00:00Z",
            "refreshToken": "fake-refresh",
            "user": {
              "id": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
              "azureTag": "{{OriginalTag}}",
              "email": "{{SessionEmail}}",
              "firstName": "Cookie",
              "lastName": "User",
              "hasPin": true
            }
          },
          "message": "Login successful"
        }
        """;

    /// <summary>
    /// Scripts the API and records what the BFF asked it. The recording matters for one assertion in
    /// particular: that the handle cached is the one the API RETURNED, not the one the caller sent.
    /// </summary>
    private sealed class Upstream
    {
        public List<string> Paths { get; } = [];

        /// <summary>What /api/users/me/azuretag answers with. Overridable per test.</summary>
        public Func<HttpResponseMessage> RenameResponse { get; set; } =
            () => Json(HttpStatusCode.OK, $$"""{"data":{"azureTag":"{{RenamedTag}}"},"message":null}""");

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);

            if (path == "/api/auth/login")
            {
                return Json(HttpStatusCode.OK, LoginSuccessJson());
            }
            if (path == "/api/users/me/azuretag")
            {
                return RenameResponse();
            }
            return Json(HttpStatusCode.OK, """{"message":"ok"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private readonly WebApplicationFactory<Program> _factory;

    public AzureTagRenameTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private (WebApplicationFactory<Program> Host, Upstream Upstream) NewHost()
    {
        var upstream = new Upstream();
        var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(upstream.Respond))));
        return (host, upstream);
    }

    private static async Task<HttpClient> SignedIn(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/bff/auth/login", new { email = SessionEmail, password = GoodPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Carry the session cookie by hand: CreateClient() does not run a cookie jar.
        var cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static async Task<string> TagFromMe(HttpClient client)
    {
        var me = await client.GetAsync("/bff/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("user").GetProperty("azureTag").GetString()!;
    }

    [Fact]
    public async Task ARenameIsVisibleOnTheVerySameSession()
    {
        var (host, _) = NewHost();
        var client = await SignedIn(host);
        (await TagFromMe(client)).Should().Be(OriginalTag);

        var response = await client.PatchAsJsonAsync("/bff/auth/azuretag", new { azureTag = RenamedTag });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await TagFromMe(client)).Should().Be(RenamedTag,
            "the whole point: no re-login, and no re-fetch that returns the same cached value");
    }

    [Fact]
    public async Task TheHandleCachedIsTheOneTheApiReturned_NotTheOneTheCallerSent()
    {
        // The server normalises. Caching the REQUEST would put a value in the session that the
        // database does not hold — a stale cache pointing the other way, and harder to notice.
        var (host, upstream) = NewHost();
        upstream.RenameResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":{"azureTag":"normalised_by_server"},"message":null}""",
                Encoding.UTF8, "application/json"),
        };
        var client = await SignedIn(host);

        // Lowercase on purpose: AzureTagPattern is ^[a-z][a-z0-9_]{2,19}$, and the BFF endpoint binds
        // the SAME shared DTO, so its [AzureTagQuery] annotation rejects a mixed-case handle with 400
        // before the action runs. A first draft of this test sent "WhatTheCallerAsked", then a 21-char one, and both failed —
        // which is how the endpoint's validation got confirmed rather than assumed.
        var response = await client.PatchAsJsonAsync(
            "/bff/auth/azuretag", new { azureTag = "caller_asked" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the request itself is valid");
        (await TagFromMe(client)).Should().Be("normalised_by_server");
    }

    [Fact]
    public async Task AConflictIsForwardedAndTheCachedHandleIsUntouched()
    {
        // ADR-0015 keeps the rename's 409 specific rather than neutral. It must ride through, and a
        // refused rename must not move the cache.
        var (host, upstream) = NewHost();
        upstream.RenameResponse = () => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                """{"title":"Conflict","status":409,"errorCode":"AZURE_TAG_TAKEN"}""",
                Encoding.UTF8, "application/problem+json"),
        };
        var client = await SignedIn(host);

        var response = await client.PatchAsJsonAsync("/bff/auth/azuretag", new { azureTag = "taken_one" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TagFromMe(client)).Should().Be(OriginalTag, "a refused rename changes nothing");
    }

    [Fact]
    public async Task WithoutASessionItIs401AndTheApiIsNeverCalled()
    {
        var (host, upstream) = NewHost();
        var client = host.CreateClient();

        var response = await client.PatchAsJsonAsync("/bff/auth/azuretag", new { azureTag = "anything" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        upstream.Paths.Should().NotContain("/api/users/me/azuretag",
            "an anonymous caller must not reach the backend at all");
    }
}
