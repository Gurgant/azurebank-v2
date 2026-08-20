using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace AzureBank.Bff.Tests;

/// <summary>
/// First coverage of AuthLevelMiddleware — security-load-bearing now that
/// GET /api/accounts/{id}/full-number discloses the unmasked account number: this
/// middleware is the ONLY PIN (level-2) gate in the system (the API has no auth-level
/// concept; the JWT carries no level claim). YARP's forwarder is replaced with a
/// recording fake, so "the backend was never called" is asserted, never assumed.
/// </summary>
public partial class AuthLevelMiddlewareTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<WebApplicationFactory<Program>> _derivedFactories = [];

    public AuthLevelMiddlewareTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public void Dispose()
    {
        // WithWebHostBuilder spins up a whole derived host per test — release them.
        foreach (var factory in _derivedFactories)
        {
            factory.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>Captures every request YARP forwards instead of hitting a real backend.</summary>
    private sealed class RecordingForwarder : IForwarderHttpClientFactory
    {
        public List<string> ForwardedPaths { get; } = [];

        /// <summary>
        /// The Authorization header AS THE BACKEND WOULD SEE IT, per forwarded request (null when
        /// none was sent). Recording the path alone cannot express the invariant that matters most
        /// here — that a token the CLIENT supplied never reaches the API — because the request is
        /// forwarded either way and only the header distinguishes the two cases.
        /// </summary>
        public List<string?> ForwardedAuthorization { get; } = [];

        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) =>
            new(new Handler(this));

        private sealed class Handler(RecordingForwarder owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.ForwardedPaths.Add(request.RequestUri!.AbsolutePath);
                owner.ForwardedAuthorization.Add(request.Headers.Authorization?.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":null,"message":"proxied"}""", Encoding.UTF8, "application/json")
                });
            }
        }
    }

    /// <summary>A request carrying a client-supplied bearer and NO session cookie.</summary>
    private static HttpRequestMessage BearerOnly(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Authorization", $"Bearer {StolenToken}");
        return request;
    }

    /// <summary>
    /// Stands in for a JWT the caller obtained for themselves. It does not need to be a valid
    /// token: the point is that the BFF must not hand it to the backend, which is decided before
    /// anything validates it.
    /// </summary>
    private const string StolenToken = "client-supplied.jwt.value";

    private (WebApplicationFactory<Program> Factory, RecordingForwarder Backend) WithRecorder(
        Action<IWebHostBuilder>? configure = null)
    {
        var recorder = new RecordingForwarder();
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(recorder)));
        });
        _derivedFactories.Add(factory);
        return (factory, recorder);
    }

    private static (string SessionId, string CookieName, ISessionService Sessions) CreateSession(
        WebApplicationFactory<Program> factory)
    {
        var sessions = factory.Services.GetRequiredService<ISessionService>();
        var cookieName = factory.Services
            .GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        var sessionId = sessions.CreateSession(
            "fake-jwt",
            DateTime.UtcNow.AddHours(1),
            "fake-refresh",
            new UserLoginInfo
            {
                Id = Guid.NewGuid(),
                AzureTag = "stepupuser",
                Email = "stepup@example.com",
                FirstName = "Step",
                LastName = "Up",
                HasPin = true
            });
        return (sessionId, cookieName, sessions);
    }

    private static HttpRequestMessage Request(
        HttpMethod method, string path, string cookieName, string sessionId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{cookieName}={sessionId}");
        return request;
    }

    /// <summary>
    /// The BFF's refusal must be INDISTINGUISHABLE from the API's own missing-token 401.
    ///
    /// <para>
    /// Two reasons, and the second is why it is asserted rather than left to taste. The SPA already
    /// understands this envelope, so nothing downstream learns a new shape. And a caller probing for
    /// which paths carry the step-up gate learns nothing from the response: a gated path with no
    /// session and an ungated one with no session answer identically.
    /// </para>
    /// <para>
    /// The literals come from the running API, observed on /api/accounts with no Authorization
    /// header, not from a doc:
    /// <c>{"type":"https://httpstatuses.com/401","title":"Unauthorized","status":401,
    /// "detail":"Authentication is required to access this resource.","instance":"/api/accounts",
    /// "errorCode":"AUTH_TOKEN_MISSING","traceId":"…"}</c>
    /// </para>
    /// </summary>
    private static async Task AssertLooksLikeTheApisOwn401(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().Be("https://httpstatuses.com/401");
        body.GetProperty("status").GetInt32().Should().Be(401);
        body.GetProperty("title").GetString().Should().Be("Unauthorized");
        body.GetProperty("detail").GetString()
            .Should().Be("Authentication is required to access this resource.");
        body.GetProperty("errorCode").GetString().Should().Be("AUTH_TOKEN_MISSING");
        body.TryGetProperty("traceId", out _).Should().BeTrue();
    }

    private static async Task AssertStepUp403(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.GetValues("X-Auth-Level-Required").Should().ContainSingle()
            .Which.Should().Be("2");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().Be("STEP_UP_REQUIRED");
        body.GetProperty("requiredLevel").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task FullNumber_AtLevel1_Is403StepUp_AndTheBackendIsNeverCalled()
    {
        var (factory, backend) = WithRecorder();
        var (sessionId, cookieName, _) = CreateSession(factory);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Request(
            HttpMethod.Get, $"/api/accounts/{Guid.NewGuid()}/full-number", cookieName, sessionId));

        await AssertStepUp403(response);
        backend.ForwardedPaths.Should().BeEmpty(
            "a level-1 session must be short-circuited BEFORE the proxy — the API would serve the number");
    }

    [Fact]
    public async Task FullNumber_WithTrailingSlash_IsGatedToo()
    {
        // Endpoint routing tolerates one trailing slash, so "/full-number/" reaches the
        // API endpoint — a raw suffix match would let it BYPASS the PIN gate entirely.
        var (factory, backend) = WithRecorder();
        var (sessionId, cookieName, _) = CreateSession(factory);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Request(
            HttpMethod.Get, $"/api/accounts/{Guid.NewGuid()}/full-number/", cookieName, sessionId));

        await AssertStepUp403(response);
        backend.ForwardedPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task TransfersPost_WithTrailingSlashAndNoSession_IsStillRefusedLocally()
    {
        /*
          The normalization hole PR #64 closed, re-pointed at the gate that still covers transfers.

          "/api/transfers/" routes to the transfers endpoint but is not equal to "/api/transfers",
          so an un-normalized exact match would miss it. Since ADR-0041 the PIN for a transfer is
          verified by the API, not here — but the SESSION check stayed behind, and it has to keep
          normalizing or it inherits the same hole the PIN gate had.
        */
        var (factory, backend) = WithRecorder();
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers/")
        {
            Content = JsonContent.Create(new { })
        };
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        backend.ForwardedPaths.Should().BeEmpty(
            "no session is refused by the BFF itself, trailing slash or not");
        await AssertLooksLikeTheApisOwn401(response);
    }

    [Fact]
    public async Task TransfersPost_WithASessionButNoPin_IsNowForwarded()
    {
        /*
          THE BEHAVIOUR CHANGE OF ADR-0041, asserted rather than described.

          This exact request used to be answered 403 STEP_UP_REQUIRED here, off a session flag that
          stayed hot for five minutes after one PIN entry. It is now forwarded, and the API rejects
          it unless the body carries the PIN — a check this process cannot make and cannot be relied
          on to have made.

          If this ever goes back to 403, the in-band check has been double-gated and the weaker of
          the two is deciding again.
        */
        var (factory, backend) = WithRecorder();
        var (sessionId, cookieName, _) = CreateSession(factory);
        var client = factory.CreateClient();

        var request = Request(HttpMethod.Post, "/api/transfers/", cookieName, sessionId);
        request.Content = JsonContent.Create(new { });
        var response = await client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "the BFF no longer answers step-up for transfers");
        response.Headers.Should().NotContainKey("X-Auth-Level-Required");
        backend.ForwardedPaths.Should().Contain("/api/transfers/",
            "a level-1 session is enough to REACH the API; the PIN is checked there");
    }

    [Fact]
    public async Task FullNumber_AfterPinVerification_IsProxiedThrough()
    {
        var (factory, backend) = WithRecorder();
        var (sessionId, cookieName, sessions) = CreateSession(factory);
        sessions.SetPinVerified(sessionId);
        var client = factory.CreateClient();

        var path = $"/api/accounts/{Guid.NewGuid()}/full-number";
        var response = await client.SendAsync(Request(HttpMethod.Get, path, cookieName, sessionId));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "level 2 satisfies the gate");
        backend.ForwardedPaths.Should().ContainSingle(p => p == path);
    }

    [Fact]
    public async Task FullNumber_AfterPinExpiry_IsGatedAgain()
    {
        // PinValidityMinutes=0 makes the elevation expire immediately: GetAuthLevel must
        // downgrade the session back to level 1 and the gate must close again.
        var (factory, backend) = WithRecorder(builder =>
            builder.UseSetting("Security:PinValidityMinutes", "0"));
        var (sessionId, cookieName, sessions) = CreateSession(factory);
        sessions.SetPinVerified(sessionId);
        await Task.Delay(30); // any elapsed time > 0-minute validity

        var client = factory.CreateClient();
        var response = await client.SendAsync(Request(
            HttpMethod.Get, $"/api/accounts/{Guid.NewGuid()}/full-number", cookieName, sessionId));

        await AssertStepUp403(response);
        backend.ForwardedPaths.Should().BeEmpty();
    }

    /*
      THE BYPASS, and why these four exist.

      Measured on the running stack (API :7215, BFF :5000, seeded dev database) before any of this
      was written — not reasoned about:

        POST /api/auth/login  (a PROXIED route)                      -> 200, raw JWT in the body
        GET  /api/accounts/{id}/full-number + Bearer, NO cookie      -> 200 "AB-0000-0000-01"
        GET  same with NO Authorization header                       -> 401   (the header IS honoured)
        POST /api/transfers + Bearer, NO cookie, bogus recipient     -> 404 from /api/transfers,
                                                                        i.e. the gate was passed

      Two defects compounded. The gate only ran INSIDE `if (Cookies.TryGetValue(...))`, and its else
      branch fell through on the belief that the API would answer 401 — which is false the moment the
      caller brings their own token. And the transform only ever SET the Authorization header inside
      that same cookie branch, never clearing an inbound one, while YARP's default header copy had
      already placed the client's on the outbound request.

      The class docstring above is the reason this is severe rather than untidy: this middleware is
      the only PIN gate in the system, so bypassing it leaves NO second factor on a transfer or a
      reveal for anyone holding the password.
    */

    [Fact]
    public async Task FullNumber_WithAClientBearerAndNoSession_IsNotProxied()
    {
        var (factory, backend) = WithRecorder();
        var client = factory.CreateClient();

        var response = await client.SendAsync(
            BearerOnly(HttpMethod.Get, $"/api/accounts/{Guid.NewGuid()}/full-number"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "no session means no caller — and the BFF must decide that itself rather than delegate "
            + "it to an API that would happily authenticate the token the client brought");
        backend.ForwardedPaths.Should().BeEmpty(
            "this is the request that returned the unmasked account number on the live stack");
        await AssertLooksLikeTheApisOwn401(response);
    }

    [Fact]
    public async Task TransfersPost_WithAClientBearerAndNoSession_IsNotProxied()
    {
        var (factory, backend) = WithRecorder();
        var client = factory.CreateClient();

        var request = BearerOnly(HttpMethod.Post, "/api/transfers");
        request.Content = JsonContent.Create(new { });
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        backend.ForwardedPaths.Should().BeEmpty(
            "on the live stack this reached the endpoint — a real recipient would have been paid "
            + "with no PIN ever entered");
        await AssertLooksLikeTheApisOwn401(response);
    }

    [Fact]
    public async Task AClientSuppliedAuthorizationHeaderIsReplacedByTheSessionsToken()
    {
        // The gate is satisfied honestly here, so nothing short-circuits and the request really is
        // forwarded. What must not survive the hop is the CLIENT's token: the BFF injects the one it
        // holds server-side, and an inbound header must never win or linger.
        var (factory, backend) = WithRecorder();
        var (sessionId, cookieName, sessions) = CreateSession(factory);
        sessions.SetPinVerified(sessionId);
        var client = factory.CreateClient();

        var path = $"/api/accounts/{Guid.NewGuid()}/full-number";
        var request = Request(HttpMethod.Get, path, cookieName, sessionId);
        request.Headers.Add("Authorization", $"Bearer {StolenToken}");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        backend.ForwardedPaths.Should().ContainSingle(p => p == path);
        backend.ForwardedAuthorization.Should().ContainSingle()
            .Which.Should().Be("Bearer fake-jwt",
                "the session's stored token, never the one the caller sent");
    }

    [Theory]
    [InlineData("GET", "/api/accounts")]
    [InlineData("PATCH", "/api/users/me/azuretag")]
    [InlineData("DELETE", "/api/accounts/01a01234-0000-7000-8000-000000000000")]
    public async Task ASessionlessNonPostIsRefusedHere_AndNeverForwarded(string method, string path)
    {
        /*
          THIS TEST USED TO ASSERT THE OPPOSITE, and the inversion is the whole change.

          It read: "/api/accounts is not PIN-gated, so the middleware never looks at it — which is
          exactly why the transform has to be the one that strips." True at the time, and it was the
          honest description of a gate that only covered POST. Every GET, PATCH and DELETE under
          /api/ was forwarded sessionless and refused by the API, so the refusal rested on
          BearerTokenTransformProvider clearing the inbound header — one line in another file.

          Now the gate has no method condition, so the request never leaves the BFF at all. That is
          strictly stronger, and it is what the old comment said was missing. The transform still
          matters and is still pinned: the test above it drives the WITH-session case and asserts the
          forwarded Authorization is the session's token and not the caller's.

          A Theory across three verbs rather than one Fact, because "POST-only" was exactly the kind
          of gap a single case cannot show.
        */
        var (factory, backend) = WithRecorder();
        var client = factory.CreateClient();

        var response = await client.SendAsync(BearerOnly(new HttpMethod(method), path));

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "no session means not signed in, which the SPA routes to login — 401, not the 403 that "
            + "would put a PIN prompt in front of someone with no session at all");
        backend.ForwardedPaths.Should().BeEmpty(
            "the refusal is decided HERE; a caller with no session must not reach the API even to "
            + "be turned away by it");
    }

    /*
      WHICH POSTS THE GATE COVERS — the question the four tests below answer.

      Until this change the covered set was an allowlist of two, "/api/transfers" and
      "/api/transfers/internal", so every other money endpoint was refused by the API instead of
      here. Measured on the running stack (API :5068, BFF :5000) before the change, sessionless and
      with no Authorization header:

        POST /api/transfers                            -> 401  decided by the BFF
        POST /api/transfers/authorizations             -> 401  decided by the API
        POST /api/transfers/internal/authorizations    -> 401  decided by the API
        POST /api/transactions/deposit                 -> 401  decided by the API
        POST /api/transactions/withdraw                -> 401  decided by the API
        POST /api/auth/pin                             -> 401  decided by the API
        POST /api/accounts                             -> 401  decided by the API
        POST /api/auth/login                           -> 400  reachable, and must stay so
        POST /api/auth/register                        -> 400  reachable, and must stay so
        POST /api/auth/refresh                         -> 404  hard-blocked above, before this gate

      The two 401s are byte-identical apart from traceId, so this is not a bypass and closing it
      changes no response a client can observe — only who produced it. What it buys is that the
      refusal stops depending on BearerTokenTransformProvider.cs:57 continuing to clear the inbound
      Authorization header. That line is the reason the gap is not exploitable today, and its absence
      is exactly the live bypass recorded above.
    */

    /// <summary>Every POST path the committed OpenAPI spec declares, in file order.</summary>
    /*
      EVERY VERB, NOT ONLY POST — and the narrowing this replaces is exactly how the gap it was meant
      to catch survived. While the gate was POST-only, filtering the spec on `post` was consistent
      with it, and that consistency is the trap: the sweep could not see the GET/PATCH/DELETE surface
      that had no gate at all, so it reported green about the half that was covered and said nothing
      about the half that was not. A drift sweep shaped like the rule it checks can only ever confirm
      the rule's own blind spots.

      HEAD and OPTIONS are not in the spec and are not synthesised here: the gate matches on the /api/
      prefix regardless of method, so they are covered by construction — but this sweep sends real
      requests, and inventing verbs the API does not declare would test the test rather than the API.
    */
    private static readonly string[] SpecVerbs =
        ["get", "post", "put", "patch", "delete"];

    private static IEnumerable<(string Path, HttpMethod Method)> SpecOperations()
    {
        var spec = Path.Combine(RepoRoot(), "docs", "api", "openapiv1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(spec));
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var verb in path.Value.EnumerateObject())
            {
                if (SpecVerbs.Contains(verb.Name, StringComparer.OrdinalIgnoreCase))
                {
                    yield return (path.Name, new HttpMethod(verb.Name.ToUpperInvariant()));
                }
            }
        }
    }

    /// <summary>
    /// Walks up from the test binary to the repository root, the same way the architecture tests do
    /// (<c>SecurityEventConstantTests.RepoBackendRoot</c>, which anchors on AzureBank.slnx) — and for
    /// the same stated reason: a guard that cannot find its input must fail loudly, because a drift
    /// sweep that silently finds no cases reports green while asserting nothing. Anchored on the spec
    /// itself rather than the solution file, so the thing being located is the thing being tested for.
    /// </summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "docs", "api", "openapiv1.json")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull(
            because: "the sweep reads its cases from docs/api/openapiv1.json; not finding it must be "
                + "a failure, never an empty sweep");
        return directory!.FullName;
    }

    [Theory]
    [InlineData("/api/transfers/authorizations")]
    [InlineData("/api/transfers/internal/authorizations")]
    [InlineData("/api/transactions/deposit")]
    [InlineData("/api/transactions/withdraw")]
    public async Task AMoneyPost_WithNoSession_IsRefusedHereAndNeverForwarded(string path)
    {
        /*
          The four that used to fall through. The mint endpoints are the sharpest of them: they
          authorise an amount to a payee (ADR-0042), so an unauthenticated caller reaching one is
          reaching the step-up factor itself rather than merely the payment it protects.

          Falsified by restoring the old allowlist: all four go red.
        */
        var (factory, backend) = WithRecorder();
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { })
        };
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        backend.ForwardedPaths.Should().BeEmpty(
            "no session is a refusal this process makes, not one it delegates to the API");
        await AssertLooksLikeTheApisOwn401(response);
    }

    [Fact]
    public async Task AMintPost_WithATrailingSlash_IsRefusedToo()
    {
        // The normalization hole PR #64 closed, re-pointed at the endpoint ADR-0042 added.
        // "/api/transfers/authorizations/" reaches the endpoint, so the gate must match the
        // normalized path or the slash alone walks past it.
        var (factory, backend) = WithRecorder();
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transfers/authorizations/")
        {
            Content = JsonContent.Create(new { })
        };
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        backend.ForwardedPaths.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/register")]
    public async Task TheProxiedAuthPair_NeverReachesTheApi_AndHandsOutNothing(string path)
    {
        /*
          THE PROPERTY THIS CHANGE EXISTS FOR, named rather than left implicit in the drift sweep.

          MEASURED on the running stack before the change, with no session cookie at all:
            POST /bff/auth/register            -> 201   (the legitimate door)
            POST /api/auth/login               -> 200, body carried data.token — a 392-character
                                                  API JWT — and a refreshToken
            that token, straight to the API:
            GET  https://localhost:7215/api/accounts -> 200   full account access
            POST /api/auth/register            -> 201   a sessionless caller could create accounts

          The BFF was issuing the credential it exists to withhold. The only thing containing it was
          BearerTokenTransformProvider clearing the inbound header on the replay path — a defence on
          a different path from the one handing the token out.

          Asserting the BODY as well as the status is the point: a 404 whose body still carried a
          token would satisfy a status-only assertion and leak everything that matters.
        */
        var (factory, backend) = WithRecorder();
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { email = "someone@example.com", password = "whatever" })
        };
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "as if the route did not exist — the caller is told nothing about why");
        backend.ForwardedPaths.Should().BeEmpty("the request must never reach the API at all");
        body.Should().NotContain("token", "no credential may leave the BFF on this path");
    }

    [Fact]
    public async Task LoginWithATrailingSlash_IsBlockedLikeTheBarePath()
    {
        /*
          The trailing-slash normalisation still matters, and it has flipped direction along with the
          rule it serves. It used to keep "/api/auth/login/" REACHABLE, because a slash alone must
          not demand a session in order to obtain one. Now that the proxied pair has no legitimate
          caller at all, the same normalisation has to keep the slashed form BLOCKED — endpoint
          routing tolerates the trailing slash, so raw matching would leave a one-character bypass of
          the block.

          Falsified by removing TrimEnd('/') from the new branch: this goes red while every other
          guard in the file stays green.
        */
        var (factory, backend) = WithRecorder();
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login/")
        {
            Content = JsonContent.Create(new { })
        };
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the slashed form is answered as if the route did not exist, exactly like the bare one");
        backend.ForwardedPaths.Should().BeEmpty(
            "a trailing slash must not be a way around the block");
    }

    [Fact]
    public async Task AMintPost_WithALevel1Session_IsForwarded()
    {
        /*
          The gate asks "is there a caller at all", never "has the caller just proved it is them".
          Minting IS the proof — the PIN travels in the body and the API verifies it (ADR-0041) — so
          answering 403 here would demand a PIN before the endpoint that exists to take one.

          If this ever goes red with a 403, the gate has grown a second question it must not ask.
        */
        var (factory, backend) = WithRecorder();
        var (sessionId, cookieName, _) = CreateSession(factory);
        var client = factory.CreateClient();

        var request = Request(HttpMethod.Post, "/api/transfers/authorizations", cookieName, sessionId);
        request.Content = JsonContent.Create(new { });
        var response = await client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        backend.ForwardedPaths.Should().Contain("/api/transfers/authorizations");
    }

    [Fact]
    public async Task NothingInTheSpecReachesTheBackendWithoutASession()
    {
        /*
          THE DRIFT SWEEP, and the reason it is written this way.

          A test that restates the middleware's own constant is a tautology: it goes green forever and
          proves nothing. This one enumerates the POST paths from the committed OpenAPI spec — which
          is generated from the backend and is the contract — and drives each one through the real
          pipeline, asserting where it ends up. So it goes RED when someone adds a POST endpoint to
          the API and regenerates the spec without deciding whether it needs a session, which is the
          failure this task exists to prevent.

          The assertion is deliberately about the BACKEND being reached rather than about the status
          code: /api/auth/refresh is answered 404 by the hard block above and never forwarded, and
          collapsing both refusals into one property keeps the sweep about the invariant that matters
          — nothing gets through — instead of the shape each refusal happens to take.

          NO EXCEPTIONS LEFT, AND NO VERB LEFT OUT. The sweep used to exempt /api/auth/login and
          /api/auth/register, and to enumerate POST paths only. Both narrowings are gone: the pair is
          404'd, and the gate no longer has a method condition. The verb half mattered most — while
          the sweep filtered on `post` it was shaped like the rule it checked, so it could not see
          the GET/PATCH/DELETE surface that had no gate at all.

          TEMPLATED PATHS ARE SUBSTITUTED, not skipped. The previous version asserted there were none,
          which held only because no POST path carried an {id}; half the reads do. Sending
          "/api/accounts/{id}" literally would probe a route nobody can call and report green about
          it, so each placeholder gets a real GUID and the request reaches a real endpoint.

          Falsified by making RequiresSession return false: every operation flips to forwarded.
        */
        var operations = SpecOperations().ToList();

        operations.Should().HaveCountGreaterThan(20, "the spec is the source of cases; an empty or "
            + "truncated read must fail loudly rather than sweep nothing");
        operations.Select(o => o.Method.Method).Distinct().Should().Contain(
            ["GET", "POST"],
            "a sweep that lost its non-POST cases would be back to checking only the half that was "
            + "already covered — the exact blind spot this widening exists to remove");

        foreach (var (template, method) in operations)
        {
            var path = TemplatePlaceholder().Replace(template, "01a01234-0000-7000-8000-000000000000");
            var (factory, backend) = WithRecorder();
            var client = factory.CreateClient();

            var request = new HttpRequestMessage(method, path);
            if (method != HttpMethod.Get && method != HttpMethod.Delete)
            {
                request.Content = JsonContent.Create(new { });
            }

            await client.SendAsync(request);

            backend.ForwardedPaths.Should().BeEmpty(
                $"{method} {path} is under /api/; with no session the BFF must refuse it itself "
                + "rather than let an unauthenticated caller reach the API");
        }
    }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex TemplatePlaceholder();
}
