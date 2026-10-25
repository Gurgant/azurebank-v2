using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What the API answers a bearer token it will not take: one that has expired, one signed with
/// another key, one for another audience, one that is not a token, and one it signed whose
/// subject is not a user id. Each is a 401 in the problem shape with its own error code. The
/// first four stop at the bearer handler; the last passes it and is refused where the endpoint
/// reads the caller's id.
/// </summary>
/// <remarks>
/// <para>
/// The API host itself, in memory, with tokens made here from the host's own <c>Jwt</c> options:
/// the signing key, the issuer and the audience are the ones it checks against, so each token is
/// wrong in exactly one way. The suite held a missing token (<c>AUTH_TOKEN_MISSING</c>) and
/// nothing else of this table; the BFF tells an expired token from a forged one by these codes.
/// </para>
/// <para>
/// A REFUSAL ONLY MEANS SOMETHING BESIDE AN ACCEPTANCE. <see cref="ATokenMadeTheSameWay_ThatIsRightInEveryWay_IsAccepted"/>
/// is that: the same maker, with nothing wrong, is answered 200.
/// </para>
/// </remarks>
public sealed class BearerTokenRefusalTests : IntegrationTestBase
{
    private const string AnotherKey = "integration-tests-only-a-signing-key-the-api-does-not-hold-0123456789";

    /// <summary>
    /// The media type of every refusal the application writes itself, as the published contract
    /// declares it: plain JSON in the problem shape, not <c>application/problem+json</c>.
    /// </summary>
    private const string TheApplicationsOwnJson = "application/json; charset=utf-8";

    public BearerTokenRefusalTests(CustomWebApplicationFactory factory) : base(factory)
    {
    }

    /// <summary>
    /// A token for <paramref name="userId"/>, as the API makes one unless an argument says otherwise.
    /// A <paramref name="subject"/> takes the place of the user id: the token then carries that
    /// <c>sub</c> and nothing else that names the caller.
    /// </summary>
    private string Token(
        Guid userId, string? signedWith = null, string? audience = null, DateTime? expires = null, string? subject = null)
    {
        var options = Factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signedWith ?? options.Secret));
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(10);
        Claim[] caller = subject is null
            ?
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            ]
            : [new Claim(JwtRegisteredClaimNames.Sub, subject)];
        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: audience ?? options.Audience,
            claims: caller,
            notBefore: expiry.AddHours(-1),
            expires: expiry,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<HttpResponseMessage> ListAccountsWithAsync(string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/accounts");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await Client.SendAsync(request);
    }

    [Fact]
    public async Task ATokenMadeTheSameWay_ThatIsRightInEveryWay_IsAccepted()
    {
        var (_, userId, _) = await RegisterTestUserAsync();

        var response = await ListAccountsWithAsync(Token(userId));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the maker of these tokens makes tokens the API takes");
    }

    [Fact]
    public async Task AnExpiredToken_Is401_AUTH_TOKEN_EXPIRED_AndSaysSoInAHeader()
    {
        // No tolerance on the expiry (ClockSkew is zero): a second past is past.
        // Measured 2026-10-12: 401, AUTH_TOKEN_EXPIRED, Token-Expired: true.
        var (_, userId, _) = await RegisterTestUserAsync();

        var response = await ListAccountsWithAsync(Token(userId, expires: DateTime.UtcNow.AddSeconds(-5)));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.ToString().Should().Be(TheApplicationsOwnJson);
        body.GetProperty("errorCode").GetString().Should().Be("AUTH_TOKEN_EXPIRED");
        body.GetProperty("detail").GetString().Should().Be("The authentication token has expired.");
        body.GetProperty("instance").GetString().Should().Be("/api/accounts");
        response.Headers.TryGetValues("Token-Expired", out var expired).Should().BeTrue();
        expired.Should().Equal("true");
    }

    [Theory]
    // Measured 2026-10-12, each against the running host.
    [InlineData("signed with another key", "AUTH_TOKEN_INVALID", "The authentication token signature is invalid.")]
    [InlineData("for another audience", "AUTH_TOKEN_INVALID", "The authentication token validation failed.")]
    [InlineData("not a token", "AUTH_TOKEN_MALFORMED", "The authentication token format is invalid.")]
    public async Task ATokenTheApiWillNotTake_Is401_WithTheCodeOfWhatIsWrong_AndIsNotCalledExpired(
        string wrong, string errorCode, string detail)
    {
        var (_, userId, _) = await RegisterTestUserAsync();
        var bearer = wrong switch
        {
            "signed with another key" => Token(userId, signedWith: AnotherKey),
            "for another audience" => Token(userId, audience: "some-other-service"),
            "not a token" => "this-is-not-a-jwt",
            _ => throw new ArgumentOutOfRangeException(nameof(wrong), wrong, "not a case of this test"),
        };

        var response = await ListAccountsWithAsync(bearer);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.ToString().Should().Be(TheApplicationsOwnJson);
        body.GetProperty("status").GetInt32().Should().Be(401);
        body.GetProperty("errorCode").GetString().Should().Be(errorCode);
        body.GetProperty("detail").GetString().Should().Be(detail);
        response.Headers.Contains("Token-Expired").Should().BeFalse(
            "a client that renews on that header must not renew for a token that was never good");
        body.GetRawText().Should().NotContain("IDX", "the library's own message, with its key details, stays in the log");
    }

    [Fact]
    public async Task ATokenSignedByTheApi_WithNoUsableUserId_Is401_AUTH_TOKEN_INVALID_NotA500()
    {
        // Signed, issued and addressed rightly, so the bearer handler takes it: what is wrong is
        // only that its subject is not a user id, which the endpoint is the first to read.
        var response = await ListAccountsWithAsync(Token(Guid.Empty, subject: "not-a-user-id"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.ToString().Should().Be(TheApplicationsOwnJson);
        body.GetProperty("errorCode").GetString().Should().Be("AUTH_TOKEN_INVALID");
        body.GetProperty("detail").GetString().Should().Be("A valid user identity is required.");
        body.GetProperty("instance").GetString().Should().Be("/api/accounts");
        response.Headers.Contains("Token-Expired").Should().BeFalse(
            "a client that renews on that header must not renew for a token that names nobody");
        body.GetRawText().Should().NotContain("IDX", "the library's own message stays in the log");
    }

    [Fact]
    public async Task OnAMoneyEndpoint_ATokenWithNoUsableUserId_Is401_BeforeTheMissingIdempotencyKeyIs400()
    {
        // The idempotency middleware runs before the action, and it reads both the caller and
        // the key. The caller comes first: a request from nobody is refused as that, whatever
        // else it lacks, so a client never retries with a key a request that will never be taken.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(Guid.Empty, subject: "not-a-user-id"));

        var response = await Client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        body.GetProperty("errorCode").GetString().Should().Be("AUTH_TOKEN_INVALID");
        body.GetProperty("instance").GetString().Should().Be("/api/transactions/deposit");
    }
}
