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
/// <c>GET /bff/auth/me</c> reads through to the API instead of serving the session cache verbatim.
///
/// <para>
/// PR #100 stopped ONE door from leaving that cache stale — the app's own rename now goes through
/// the BFF and writes the handle back. Measured on the running stack afterwards, a second door was
/// still wide open, and it needs no race at all: one request, a session cookie, and the proxied
/// route that YARP still serves.
/// </para>
/// <para>
/// <c>PATCH /api/users/me/azuretag</c> with only a cookie (the transform injects the token) returned
/// 200 and the database moved to <c>admin_probe1</c> — while <c>GET /bff/auth/me</c> kept answering
/// <c>admin</c>, and answered it again on re-fetch. That is verbatim the defect PR #100 was written
/// to remove, reached through a route the app no longer uses but the proxy still exposes.
/// </para>
/// <para>
/// So the cache stops being the answer and becomes the FALLBACK. The API is asked on every
/// <c>/me</c>; the cached value is served only when the API cannot be reached, which is the correct
/// degrade — <c>authSlice</c> treats a rejected <c>getMe</c> as signed-out, so failing the request
/// would turn a backend blip into a spurious logout.
/// </para>
/// </summary>
public class MeReadsThroughToTheApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string SessionEmail = "cookieuser@azurebank.dev";
    private const string GoodPassword = "Password123!";
    private const string CachedTag = "cookieuser";
    private const string UserId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    /// <summary>What login caches: the handle as it stood when the session was minted.</summary>
    private static string LoginSuccessJson() => $$"""
        {
          "data": {
            "token": "jwt-1",
            "expiresAt": "2030-01-01T00:00:00Z",
            "refreshToken": "fake-refresh",
            "user": {
              "id": "{{UserId}}",
              "azureTag": "{{CachedTag}}",
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
    /// The API's own <c>/api/auth/me</c> shape, captured off the wire rather than read off the DTO:
    /// <c>{"data":{"userId":…,"azureTag":…,"email":…,"firstName":…,"lastName":…},"message":null}</c>.
    /// Note there is no <c>hasPin</c> — that field exists only in the BFF's session, which is why
    /// the merge below keeps it from the cache instead of defaulting it to false.
    /// </summary>
    private static string ApiMeJson(string azureTag) => $$"""
        {
          "data": {
            "userId": "{{UserId}}",
            "azureTag": "{{azureTag}}",
            "email": "{{SessionEmail}}",
            "firstName": "Cookie",
            "lastName": "User"
          },
          "message": null
        }
        """;

    private sealed class Upstream
    {
        public List<string> Paths { get; } = [];

        /// <summary>The handle the API reports. Moving it simulates any out-of-band rename.</summary>
        public string ApiTag { get; set; } = CachedTag;

        /// <summary>Set to fail the /me read and exercise the fallback.</summary>
        public Func<HttpResponseMessage>? MeFailure { get; set; }

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);

            if (path == "/api/auth/login")
            {
                return Json(HttpStatusCode.OK, LoginSuccessJson());
            }
            if (path == "/api/auth/me")
            {
                return MeFailure?.Invoke() ?? Json(HttpStatusCode.OK, ApiMeJson(ApiTag));
            }
            return Json(HttpStatusCode.OK, """{"message":"ok"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private readonly WebApplicationFactory<Program> _factory;

    public MeReadsThroughToTheApiTests(WebApplicationFactory<Program> factory) => _factory = factory;

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

        var cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static async Task<JsonElement> Me(HttpClient client)
    {
        var response = await client.GetAsync("/bff/auth/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("user");
    }

    [Fact]
    public async Task AHandleChangedOutOfBandIsVisibleWithoutRelogin()
    {
        // The measured defect, in the harness: the cache says one thing, the database says another.
        var (host, upstream) = NewHost();
        var client = await SignedIn(host);
        (await Me(client)).GetProperty("azureTag").GetString().Should().Be(CachedTag);

        upstream.ApiTag = "renamed_elsewhere";

        (await Me(client)).GetProperty("azureTag").GetString().Should().Be("renamed_elsewhere",
            "the cache is the fallback now, not the answer");
    }

    [Fact]
    public async Task TheApiIsActuallyAsked()
    {
        // Guards the whole point against a regression that quietly restores the cached read: if
        // /api/auth/me stops being called, every other assertion here still passes on the cache.
        var (host, upstream) = NewHost();
        var client = await SignedIn(host);
        upstream.Paths.Clear();

        await Me(client);

        upstream.Paths.Should().Contain("/api/auth/me");
    }

    [Fact]
    public async Task AFailedReadServesTheCacheRatherThanSigningTheUserOut()
    {
        // authSlice.ts treats a rejected getMe as unauthenticated, so a 502 here would log the user
        // out on a backend blip. The degrade has to be the last known value, not an error.
        var (host, upstream) = NewHost();
        var client = await SignedIn(host);
        upstream.MeFailure = () => new HttpResponseMessage(HttpStatusCode.BadGateway);

        var user = await Me(client);

        user.GetProperty("azureTag").GetString().Should().Be(CachedTag);
    }

    [Fact]
    public async Task AnUnreachableApiAlsoServesTheCache()
    {
        // Not the same path as a 502: this one throws out of SendAsync rather than returning.
        var (host, upstream) = NewHost();
        var client = await SignedIn(host);
        upstream.MeFailure = () => throw new HttpRequestException("connection refused");

        var user = await Me(client);

        user.GetProperty("azureTag").GetString().Should().Be(CachedTag);
    }

    [Fact]
    public async Task ACancelledReadServesTheCache()
    {
        /*
          Pins the catch clause that the read-through's deadline depends on. The read is bounded by
          a linked CancellationTokenSource, and when that fires the await surfaces an
          OperationCanceledException — so a `catch` naming only TaskCanceledException would let a
          timed-out read escape as a 500 instead of degrading, which is the opposite of the point.

          What this does NOT prove is the deadline itself. FakeBackendApiHandler produces its
          response synchronously (Task.FromResult over a Func), so a responder that sleeps parks the
          calling thread before there is anything to cancel; the harness can express a failing API,
          never a slow one. Asserting on the elapsed time here would be asserting on the harness.
        */
        var (host, upstream) = NewHost();
        var client = await SignedIn(host);
        upstream.MeFailure = () => throw new OperationCanceledException("deadline");

        var user = await Me(client);

        user.GetProperty("azureTag").GetString().Should().Be(CachedTag);
    }

    [Fact]
    public async Task AMalformedBodyServesTheCache()
    {
        var (host, upstream) = NewHost();
        var client = await SignedIn(host);
        upstream.MeFailure = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json at all", Encoding.UTF8, "application/json"),
        };

        var user = await Me(client);

        user.GetProperty("azureTag").GetString().Should().Be(CachedTag);
    }

    [Fact]
    public async Task HasPinSurvivesTheReadThrough()
    {
        // The API's /me carries no hasPin. Rebuilding the user from it alone would silently flip
        // this to false and re-prompt a user who already has a PIN.
        var (host, upstream) = NewHost();
        var client = await SignedIn(host);
        upstream.ApiTag = "renamed_elsewhere";

        var user = await Me(client);

        user.GetProperty("hasPin").GetBoolean().Should().BeTrue();
        user.GetProperty("email").GetString().Should().Be(SessionEmail);
    }

    [Fact]
    public async Task AReadNeverWritesTheCache()
    {
        /*
          The read is a read. It used to write the fresh handle back to keep the fallback current,
          which meant a /me that started before a rename and finished after it could put the
          pre-rename handle back — a read overwriting a newer write.

          The cost of not writing is exactly what this test pins: after a successful read of an
          out-of-band rename, the FALLBACK still holds the login-time value. That is the documented
          degrade (ADR-0039), not an oversight, and it only surfaces if the API dies right after a
          rename made outside the app. The app's own rename writes the cache itself.
        */
        var (host, upstream) = NewHost();
        var client = await SignedIn(host);

        upstream.ApiTag = "renamed_elsewhere";
        (await Me(client)).GetProperty("azureTag").GetString().Should().Be("renamed_elsewhere");

        upstream.MeFailure = () => new HttpResponseMessage(HttpStatusCode.BadGateway);
        var user = await Me(client);

        user.GetProperty("azureTag").GetString().Should().Be(CachedTag,
            "the successful read must not have mutated the session");
    }

    [Fact]
    public async Task WithoutASessionItIs401AndTheApiIsNeverAsked()
    {
        var (host, upstream) = NewHost();
        var client = host.CreateClient();

        var response = await client.GetAsync("/bff/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        upstream.Paths.Should().NotContain("/api/auth/me");
    }
}
