extern alias bff;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using BffSessionOptions = bff::AzureBank.Bff.Options.BffSessionOptions;
using BffSessions = bff::AzureBank.Bff.Services.Interfaces.ISessionService;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Sign-in and the PIN through the real BFF in front of the real API
/// (<see cref="BffOverApiFactory"/>, InMemory database): what the browser is answered when the API
/// refuses, and what the BFF then holds.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is scripted, so the status, the error code and the body are the API's own, as the BFF
/// hands them on. The BFF's own suite scripts the API. No test anywhere ran a refused
/// registration, a wrong PIN or <c>set-pin</c> through the BFF, and a refused sign-in was held
/// only against a scripted answer.
/// </para>
/// <para>
/// <c>hasPin</c> IS THE BFF'S OWN. <c>GET /bff/auth/me</c> reads the user from the API and carries
/// <c>hasPin</c> over from the session, because the API's <c>/api/auth/me</c> does not return it.
/// So after <c>set-pin</c> the only thing that lets the same session say "this user has a PIN" is
/// the write <c>set-pin</c> makes to that session.
/// </para>
/// </remarks>
public sealed class BffSignInAndPinOverApiTests
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Password = "SecurePass123!";
    private const string Pin = "824613";

    private static HttpClient Browser(BffOverApiFactory bffHost) =>
        bffHost.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static string CookieName(BffOverApiFactory bffHost) =>
        bffHost.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;

    private static async Task<string> RegisterAsync(HttpClient apiClient)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"pin{unique}@example.com";
        var response = await apiClient.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"pin_{unique}",
            Email = email,
            Password = Password,
            FirstName = "Pin",
            LastName = "Holder",
        }, Json);
        response.EnsureSuccessStatusCode();
        return email;
    }

    /// <summary>Signs in through the BFF, as the SPA does, and returns the session the cookie names.</summary>
    private static async Task<string> SignInAsync(BffOverApiFactory bffHost, HttpClient browser, string email)
    {
        var response = await browser.PostAsJsonAsync("/bff/auth/login",
            new LoginRequest { Email = email, Password = Password }, Json);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"ARRANGE: the sign-in through the BFF must work (body: {await response.Content.ReadAsStringAsync()})");

        var prefix = CookieName(bffHost) + "=";
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(prefix, StringComparison.Ordinal));
        return cookie[prefix.Length..].Split(';')[0];
    }

    private static HttpRequestMessage WithSession(
        BffOverApiFactory bffHost, HttpMethod method, string path, string sessionId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{CookieName(bffHost)}={sessionId}");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return request;
    }

    private static async Task<bool> HasPinAccordingToMeAsync(BffOverApiFactory bffHost, HttpClient browser, string sessionId)
    {
        var me = await browser.SendAsync(WithSession(bffHost, HttpMethod.Get, "/bff/auth/me", sessionId));
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("user").GetProperty("hasPin").GetBoolean();
    }

    [Theory]
    [InlineData("a wrong password")]
    [InlineData("an address nobody registered")]
    [InlineData("a wrong password that breaks the registration rule")]
    public async Task ARefusedSignIn_ReachesTheBrowserAsTheApis401_WithNoSessionAndNoCookie(string refusal)
    {
        // One answer for all three, as the API gives it: which of them was wrong is not said.
        // Measured 2026-10-12: 401, errorCode INVALID_CREDENTIALS, no Set-Cookie. The third is no
        // longer refused by the BFF's own model state: on main at bd743911 the BFF answered it 400
        // with the [Password] message, and the API never saw it.
        using var api = new CustomWebApplicationFactory();
        var apiClient = api.CreateClient();
        using var bffHost = new BffOverApiFactory(api, CustomWebApplicationFactory.ServiceCredentialKey);
        var browser = Browser(bffHost);
        var email = await RegisterAsync(apiClient);

        var attempt = refusal switch
        {
            "a wrong password" => new LoginRequest { Email = email, Password = "NotThePassword123!" },
            "an address nobody registered" => new LoginRequest { Email = "nobody-" + email, Password = Password },
            _ => new LoginRequest { Email = email, Password = "Ab1!" },
        };
        var response = await browser.PostAsJsonAsync("/bff/auth/login", attempt, Json);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        body.GetProperty("errorCode").GetString().Should().Be("INVALID_CREDENTIALS");
        body.GetProperty("status").GetInt32().Should().Be(401);
        response.Headers.Contains("Set-Cookie").Should().BeFalse("a refused sign-in is given no session");
        body.GetRawText().Should().NotContain("accessToken");
    }

    [Fact]
    public async Task ARefusedRegistration_ReachesTheBrowserAsTheApisAnswer_WithNoSessionAndNoCookie()
    {
        // An address that is already registered. The API does not say that it is: the answer is
        // the neutral one it gives every registration it cannot complete.
        // Measured 2026-10-12: 409, errorCode REGISTRATION_FAILED, no Set-Cookie.
        using var api = new CustomWebApplicationFactory();
        var apiClient = api.CreateClient();
        using var bffHost = new BffOverApiFactory(api, CustomWebApplicationFactory.ServiceCredentialKey);
        var browser = Browser(bffHost);
        var email = await RegisterAsync(apiClient);

        var response = await browser.PostAsJsonAsync("/bff/auth/register", new RegisterRequest
        {
            AzureTag = "another_" + Guid.NewGuid().ToString("N")[..8],
            Email = email,
            Password = Password,
            FirstName = "Second",
            LastName = "Comer",
        }, Json);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        body.GetProperty("errorCode").GetString().Should().Be("REGISTRATION_FAILED");
        body.GetProperty("detail").GetString().Should().Be("Registration could not be completed.");
        body.GetRawText().Should().NotContain(email, "the answer does not say which address");
        response.Headers.Contains("Set-Cookie").Should().BeFalse("a refused registration is given no session");
    }

    [Fact]
    public async Task SettingAPin_StoresItInTheApi_AndTheSameSessionThenSaysTheUserHasOne()
    {
        // Measured 2026-10-12: /me says hasPin false, set-pin answers 200 "PIN set successfully",
        // /me then says hasPin true on the same session, and the API verifies the PIN it was sent.
        using var api = new CustomWebApplicationFactory();
        var apiClient = api.CreateClient();
        using var bffHost = new BffOverApiFactory(api, CustomWebApplicationFactory.ServiceCredentialKey);
        var browser = Browser(bffHost);
        var email = await RegisterAsync(apiClient);
        var session = await SignInAsync(bffHost, browser, email);

        var before = await HasPinAccordingToMeAsync(bffHost, browser, session);

        var set = await browser.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/set-pin", session, new { pin = Pin, password = Password }));
        var setBody = await set.Content.ReadFromJsonAsync<JsonElement>();

        var after = await HasPinAccordingToMeAsync(bffHost, browser, session);

        var verify = await browser.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/verify-pin", session, new { pin = Pin }));
        var verifyBody = await verify.Content.ReadFromJsonAsync<JsonElement>();

        using var all = new AssertionScope();
        before.Should().BeFalse("ARRANGE: a new account has no PIN");
        set.StatusCode.Should().Be(HttpStatusCode.OK);
        setBody.GetProperty("message").GetString().Should().Be("PIN set successfully");
        after.Should().BeTrue("the session that set the PIN is not asked to set one again");
        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        verifyBody.GetProperty("data").GetProperty("verified").GetBoolean().Should().BeTrue(
            "the PIN the API holds is the one the browser sent");
        verifyBody.GetProperty("data").GetProperty("authLevel").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task SettingAFirstPinWithoutThePassword_IsTheApis422_AndTheSessionStillSaysThereIsNoPin()
    {
        // A session cookie alone does not mint the credential that authorises every movement
        // (ADR-0040): the API asks for the password, and the BFF hands its refusal on as it is and
        // does not mark the session.
        // Measured 2026-10-12: 422, errorCode PASSWORD_REQUIRED, hasPin false before and after.
        using var api = new CustomWebApplicationFactory();
        var apiClient = api.CreateClient();
        using var bffHost = new BffOverApiFactory(api, CustomWebApplicationFactory.ServiceCredentialKey);
        var browser = Browser(bffHost);
        var email = await RegisterAsync(apiClient);
        var session = await SignInAsync(bffHost, browser, email);

        var set = await browser.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/set-pin", session, new { pin = Pin }));
        var body = await set.Content.ReadFromJsonAsync<JsonElement>();

        var after = await HasPinAccordingToMeAsync(bffHost, browser, session);
        var verify = await browser.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/verify-pin", session, new { pin = Pin }));

        using var all = new AssertionScope();
        set.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        body.GetProperty("errorCode").GetString().Should().Be("PASSWORD_REQUIRED");
        after.Should().BeFalse("a refused PIN is not a PIN");
        (await verify.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("verified")
            .GetBoolean().Should().BeFalse("and the API holds none: the PIN that was refused opens nothing");
    }

    [Fact]
    public async Task AWrongPin_Is200NotVerified_AndTheSessionStaysAtLevelOne()
    {
        // The API answers a wrong PIN with 200 and verified false, not with an error, and the BFF
        // must not raise the session on a 200 alone.
        // Measured 2026-10-12: 200, verified false, authLevel 1, pinExpiresAt null, "Invalid PIN".
        using var api = new CustomWebApplicationFactory();
        var apiClient = api.CreateClient();
        using var bffHost = new BffOverApiFactory(api, CustomWebApplicationFactory.ServiceCredentialKey);
        var browser = Browser(bffHost);
        var email = await RegisterAsync(apiClient);
        var session = await SignInAsync(bffHost, browser, email);
        var set = await browser.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/set-pin", session, new { pin = Pin, password = Password }));
        set.StatusCode.Should().Be(HttpStatusCode.OK, "ARRANGE: the account has a PIN");

        var response = await browser.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/verify-pin", session, new { pin = "135790" }));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var held = bffHost.Services.GetRequiredService<BffSessions>().GetSession(session)!;

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("message").GetString().Should().Be("Invalid PIN");
        var data = body.GetProperty("data");
        data.GetProperty("verified").GetBoolean().Should().BeFalse();
        data.GetProperty("authLevel").GetInt32().Should().Be(1);
        data.GetProperty("pinExpiresAt").ValueKind.Should().Be(JsonValueKind.Null);
        held.AuthLevel.Should().Be(1, "a wrong PIN raises nothing");
        held.PinVerifiedAt.Should().BeNull();
    }

    [Fact]
    public async Task APinSetInAnotherBrowser_IsLearntFromTheApisRefusal()
    {
        using var api = new CustomWebApplicationFactory();
        var apiClient = api.CreateClient();
        using var bffHost = new BffOverApiFactory(api, CustomWebApplicationFactory.ServiceCredentialKey);
        var browserA = Browser(bffHost);
        var browserB = Browser(bffHost);
        var email = await RegisterAsync(apiClient);
        var sessionA = await SignInAsync(bffHost, browserA, email);
        var sessionB = await SignInAsync(bffHost, browserB, email);

        var set = await browserA.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/set-pin", sessionA, new { pin = Pin, password = Password }));
        set.StatusCode.Should().Be(HttpStatusCode.OK, "ARRANGE: browser A sets the account's PIN");
        (await HasPinAccordingToMeAsync(bffHost, browserB, sessionB)).Should().BeFalse(
            "ARRANGE: browser B has not learnt of the PIN set elsewhere");

        var refused = await browserB.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/set-pin", sessionB, new { pin = "135790", password = Password }));
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();

        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        body.GetProperty("errorCode").GetString().Should().Be("PIN_REQUIRED");
        (await HasPinAccordingToMeAsync(bffHost, browserB, sessionB)).Should().BeTrue(
            "the refusal proves a PIN exists");
    }

    [Fact]
    public async Task AWrongPinOnAnElevatedSession_KeepsItsLevel()
    {
        using var api = new CustomWebApplicationFactory();
        var apiClient = api.CreateClient();
        using var bffHost = new BffOverApiFactory(api, CustomWebApplicationFactory.ServiceCredentialKey);
        var browser = Browser(bffHost);
        var email = await RegisterAsync(apiClient);
        var session = await SignInAsync(bffHost, browser, email);
        var set = await browser.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/set-pin", session, new { pin = Pin, password = Password }));
        set.StatusCode.Should().Be(HttpStatusCode.OK, "ARRANGE: the account has a PIN");
        var elevate = await browser.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/verify-pin", session, new { pin = Pin }));
        elevate.StatusCode.Should().Be(HttpStatusCode.OK);
        (await elevate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("authLevel")
            .GetInt32().Should().Be(2, "ARRANGE: the session is elevated");

        var response = await browser.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/verify-pin", session, new { pin = "135790" }));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var held = bffHost.Services.GetRequiredService<BffSessions>().GetSession(session)!;

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = body.GetProperty("data");
        data.GetProperty("verified").GetBoolean().Should().BeFalse();
        data.GetProperty("authLevel").GetInt32().Should().Be(2);
        held.AuthLevel.Should().Be(2);
    }

    [Fact]
    public async Task ARefusalThatProvesNoPin_LeavesHasPinFalse()
    {
        using var api = new CustomWebApplicationFactory();
        var apiClient = api.CreateClient();
        using var bffHost = new BffOverApiFactory(api, CustomWebApplicationFactory.ServiceCredentialKey);
        var browser = Browser(bffHost);
        var email = await RegisterAsync(apiClient);
        var session = await SignInAsync(bffHost, browser, email);

        var refused = await browser.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/set-pin", session, new { pin = Pin }));
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();

        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        body.GetProperty("errorCode").GetString().Should().Be("PASSWORD_REQUIRED");
        (await HasPinAccordingToMeAsync(bffHost, browser, session)).Should().BeFalse();
    }

    [Fact]
    public async Task AChangeOfPinWithTheWrongCurrentOne_IsTheApisRefusal_AndTheOldPinStillVerifies()
    {
        // The API answers a wrong current PIN with 401 INVALID_PIN, counted against the same
        // lockout as every other wrong PIN. The BFF hands it on, and it is not the 401 of a
        // session that has ended: the session is still there afterwards.
        // Measured 2026-10-12: 401, errorCode INVALID_PIN, detail "Invalid PIN.".
        using var api = new CustomWebApplicationFactory();
        var apiClient = api.CreateClient();
        using var bffHost = new BffOverApiFactory(api, CustomWebApplicationFactory.ServiceCredentialKey);
        var browser = Browser(bffHost);
        var email = await RegisterAsync(apiClient);
        var session = await SignInAsync(bffHost, browser, email);
        var set = await browser.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/set-pin", session, new { pin = Pin, password = Password }));
        set.StatusCode.Should().Be(HttpStatusCode.OK, "ARRANGE: the account has a PIN");

        var change = await browser.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/set-pin", session, new { pin = "975318", currentPin = "135790" }));
        var changeBody = await change.Content.ReadFromJsonAsync<JsonElement>();

        var withTheNew = await browser.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/verify-pin", session, new { pin = "975318" }));
        var withTheOld = await browser.SendAsync(WithSession(
            bffHost, HttpMethod.Post, "/bff/auth/verify-pin", session, new { pin = Pin }));

        using var all = new AssertionScope();
        change.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        changeBody.GetProperty("errorCode").GetString().Should().Be("INVALID_PIN");
        changeBody.GetProperty("detail").GetString().Should().Be("Invalid PIN.");
        bffHost.Services.GetRequiredService<BffSessions>().GetSession(session).Should().NotBeNull(
            "a wrong PIN is not a session that has ended");
        (await withTheNew.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("verified")
            .GetBoolean().Should().BeFalse("the refused PIN was not stored");
        (await withTheOld.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("verified")
            .GetBoolean().Should().BeTrue("the PIN the account had still opens it");
    }
}
