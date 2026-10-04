using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Exceptions;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The BFF's own doors as the public demo changes them (<c>Demo:Enabled</c>): what they answer
/// with the flag on, and with it off, as in every deployment that does not set it.
/// </summary>
/// <remarks>
/// <para>
/// One host per test, with the flag set by <c>UseSetting</c> as a deployment's variable sets it,
/// and an API that is a script: it records each call's path and body and answers by the path.
/// Calls are counted by path, never in all: the host's own client also calls
/// <c>/api/auth/revoke</c> when a session ends and <c>/api/auth/session-stamps</c> while one is
/// held.
/// </para>
/// <para>
/// The test server gives a request no address, so every request of a host is the limiter's one
/// client <c>unknown</c>, with ten calls a minute on the <c>auth</c> policy unless a test sets
/// another number.
/// </para>
/// </remarks>
public class DemoClaimTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string RegisterPath = "/bff/auth/register";
    private const string ApiRegisterPath = "/api/auth/register";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<WebApplicationFactory<Program>> _derived = [];

    public DemoClaimTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public void Dispose()
    {
        foreach (var host in _derived)
        {
            host.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The scripted API: every call it was sent, and an answer for each path.</summary>
    private sealed class Upstream
    {
        /// <summary>Each call in arrival order. A queue, because the grant revoker calls from its own thread.</summary>
        public ConcurrentQueue<(string Path, string Body)> Calls { get; } = new();

        public int CallsTo(string path) => Calls.Count(call => call.Path == path);

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Calls.Enqueue((path, body));

            return path switch
            {
                ApiRegisterPath => Json(HttpStatusCode.Created, RegisteredJson),
                _ => Json(HttpStatusCode.OK, """{"data":null,"message":"ok"}"""),
            };
        }

        public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>What the API answers a registration it accepts.</summary>
    private const string RegisteredJson = """
        {
          "data": {
            "account": {
              "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
              "accountNumber": "TEST-0000-0001",
              "name": "Conto principale",
              "type": "Checking",
              "balance": 0,
              "isPrimary": true,
              "createdAt": "2026-09-28T00:00:00Z"
            },
            "token": {
              "accessToken": "jwt-registered",
              "refreshToken": "rt-registered",
              "refreshTokenExpiresAt": "2030-01-01T01:00:00Z",
              "expiresIn": 899,
              "tokenType": "Bearer",
              "expiresAt": "2030-01-01T00:00:00Z"
            },
            "user": {
              "id": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
              "azureTag": "newcomer_2026",
              "email": "newcomer@example.com",
              "firstName": "New",
              "lastName": "Comer",
              "hasPin": false
            }
          },
          "message": "Registration successful"
        }
        """;

    /// <summary>A host with the demo on or off, in front of a scripted API of its own.</summary>
    private (WebApplicationFactory<Program> Host, Upstream Upstream) NewHost(
        bool demo, Action<IWebHostBuilder>? configure = null)
    {
        var upstream = new Upstream();
        var host = _factory.WithWebHostBuilder(builder =>
        {
            if (demo)
            {
                builder.UseSetting("Demo:Enabled", "true");
            }

            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(upstream.Respond)));
        });
        _derived.Add(host);
        return (host, upstream);
    }

    // ── Registration ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A registration the BFF's binder accepts and the scripted API answers 201.</summary>
    private const string ARegistration =
        """{"azureTag":"newcomer_2026","email":"newcomer@example.com","password":"TestPass123!","firstName":"New","lastName":"Comer"}""";

    private static HttpRequestMessage RegistrationOf(string? body, string? mediaType)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, RegisterPath);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, mediaType!);
        }

        return request;
    }

    /// <summary>Requests to registration, each with the status a host with the demo off answers it.</summary>
    public static TheoryData<string, string?, string?, int> Registrations() => new()
    {
        { "a registration that would be accepted", ARegistration, "application/json", 201 },
        { "an empty object", "{}", "application/json", 400 },
        { "a form post", "email=newcomer%40example.com", "application/x-www-form-urlencoded", 415 },
        { "plain text", "let me in", "text/plain", 415 },
        { "no body at all", null, null, 415 },
    };

    [Theory]
    [MemberData(nameof(Registrations))]
    public async Task InDemoMode_RegistrationIs403_WhateverTheBody_AndTheApiIsNeverCalled(
        string what, string? body, string? mediaType, int withTheDemoOff)
    {
        // CONTROL: where registration is open the same request is answered by the binder or by the
        // API, each in its own way. On the demo neither is reached.
        var (ordinary, _) = NewHost(demo: false);
        using var there = await ordinary.CreateClient().SendAsync(RegistrationOf(body, mediaType));
        ((int)there.StatusCode).Should().Be(
            withTheDemoOff, "CONTROL: {0}, on a host with the demo off", what);

        var (host, upstream) = NewHost(demo: true);

        using var response = await host.CreateClient().SendAsync(RegistrationOf(body, mediaType));

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "{0}: on the demo registration is closed", what);
        using var problem = JsonDocument.Parse(text);
        var root = problem.RootElement;
        using (new AssertionScope())
        {
            // The API's own refusal of a registration on the demo, member for member, written here
            // without asking it.
            root.EnumerateObject().Select(member => member.Name).Should().BeEquivalentTo(
                ["type", "title", "status", "detail", "instance", "errorCode", "traceId"]);
            root.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.RegistrationClosed);
            root.GetProperty("detail").GetString().Should().Be(RegistrationClosedException.Detail);
            root.GetProperty("status").GetInt32().Should().Be(403);
            root.GetProperty("title").GetString().Should().Be("Forbidden");
            root.GetProperty("type").GetString().Should().Be("https://httpstatuses.com/403");
            root.GetProperty("instance").GetString().Should().Be(RegisterPath);
            root.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace("a refusal can be found in the log by it");
            (response.Content.Headers.ContentType?.MediaType).Should().Be("application/json");

            upstream.CallsTo(ApiRegisterPath).Should().Be(0, "the BFF refuses by itself: the API is not asked");
            response.Headers.Contains("Set-Cookie").Should().BeFalse("no session is opened");
        }
    }

    // CONTROL: green before this change. Where the demo is off, registration is open, as it was.
    [Fact]
    public async Task WithTheFlagOff_RegistrationStillReachesTheApi()
    {
        var (host, upstream) = NewHost(demo: false);

        using var response = await host.CreateClient().SendAsync(RegistrationOf(ARegistration, "application/json"));

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        using var body = JsonDocument.Parse(text);
        using (new AssertionScope())
        {
            body.RootElement.GetProperty("data").GetProperty("user").GetProperty("email").GetString()
                .Should().Be("newcomer@example.com");
            upstream.CallsTo(ApiRegisterPath).Should().Be(1);
            upstream.Calls.Single(call => call.Path == ApiRegisterPath).Body.Should().Contain("newcomer_2026");
            response.Headers.Contains("Set-Cookie").Should().BeTrue("a registration opens a session");
        }
    }

    [Fact]
    public async Task InDemoMode_AClosedRegistration_StillSpendsTheAuthLimit()
    {
        // The limiter comes before the refusal: a closed door is still a door somebody can hammer,
        // and the client that does is told to slow down as on any other door of this policy.
        var (host, upstream) = NewHost(demo: true, builder => builder.UseSetting("RateLimiting:AuthPermitLimit", "2"));
        var client = host.CreateClient();

        using var first = await client.SendAsync(RegistrationOf(ARegistration, "application/json"));
        using var second = await client.SendAsync(RegistrationOf(ARegistration, "application/json"));
        using var third = await client.SendAsync(RegistrationOf(ARegistration, "application/json"));

        var text = await third.Content.ReadAsStringAsync();
        using (new AssertionScope())
        {
            first.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            second.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, text);
            text.Should().Contain(ErrorCodes.RateLimitExceeded);
            third.Headers.Contains("Retry-After").Should().BeTrue();
            upstream.CallsTo(ApiRegisterPath).Should().Be(0);
        }
    }

    public static TheoryData<bool, string> OtherMethods()
    {
        var rows = new TheoryData<bool, string>();
        foreach (var demoOn in new[] { false, true })
        {
            foreach (var method in new[] { "GET", "HEAD", "PUT", "PATCH", "DELETE", "OPTIONS" })
            {
                rows.Add(demoOn, method);
            }
        }

        return rows;
    }

    // CONTROL: green before this change. It pins what the host answers today, so that it is known:
    // the 403 closes the endpoint, which is POST, and not its path. Another method on the path
    // never reaches the action, so no marker of the action's is read; it is answered 405 and told
    // which method the path takes, with the demo off or on, as on the path of sign-in.
    [Theory]
    [MemberData(nameof(OtherMethods))]
    public async Task AnotherMethodOnRegistrationsPath_Is405WhateverTheFlag_AsOnSignInsPath(bool demoOn, string method)
    {
        var (host, upstream) = NewHost(demoOn);
        var client = host.CreateClient();

        using var register = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), RegisterPath));
        using var login = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/bff/auth/login"));
        using var unknown = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/bff/nope"));

        using (new AssertionScope())
        {
            register.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            register.Content.Headers.Allow.Should().Equal("POST");
            login.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, "CONTROL: sign-in's path answers another method the same way");
            login.Content.Headers.Allow.Should().Equal("POST");
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound, "CONTROL: a path with no route is 404 under any method");
            upstream.CallsTo(ApiRegisterPath).Should().Be(0);
        }
    }
}
