using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The API's endpoints as the public demo changes them, through the host: what they answer with
/// <c>Demo:Enabled</c> false, as in every deployment that does not set it.
/// </summary>
public sealed class DemoModeEndpointTests : IDisposable
{
    private const string ClaimPath = "/api/auth/demo/claim";

    private readonly CustomWebApplicationFactory _ordinary = new();

    public void Dispose() => _ordinary.Dispose();

    /// <summary>A response as status, media type and body, without the one member that differs between two requests.</summary>
    private static async Task<string> ShapeOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > 0 && JsonNode.Parse(body) is JsonObject json)
        {
            json.Remove("traceId");
            body = json.ToJsonString();
        }

        return $"{(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType} {body}";
    }

    // ── With the demo off ────────────────────────────────────────────────────────────────────────

    // CONTROL: green before this change: no route answers the path yet. Once the claim's action
    // is there, [DemoOnly] is what keeps it green: without the marker the request reaches the action.
    [Fact]
    public async Task WithTheDemoOff_TheClaimLooksLikeAPathThatDoesNotExist()
    {
        using var client = _ordinary.CreateClient();
        var body = new DemoClaimRequest { ClientAddress = "203.0.113.7" };

        using var claim = await client.PostAsJsonAsync(ClaimPath, body);
        using var unknown = await client.PostAsJsonAsync("/api/auth/no-such-endpoint", body);

        using (new AssertionScope())
        {
            claim.StatusCode.Should().Be(HttpStatusCode.NotFound);
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound, "CONTROL: no route answers that path");
            claim.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

            using var claimBody = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
            using var unknownBody = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
            claimBody.RootElement.EnumerateObject().Select(p => p.Name)
                .Should().BeEquivalentTo(unknownBody.RootElement.EnumerateObject().Select(p => p.Name));
            claimBody.RootElement.GetProperty("title").GetString()
                .Should().Be(unknownBody.RootElement.GetProperty("title").GetString());

            // And everything else: status, media type, and each member but the trace id.
            (await ShapeOfAsync(claim)).Should().Be(
                await ShapeOfAsync(unknown),
                "a deployment that is not the demo tells a caller nothing a path with no route would not");
        }
    }

    public static TheoryData<string, string?, string?> MalformedClaims() => new()
    {
        { "an empty object", "{}", "application/json" },
        { "a client address of 65 characters", $$"""{"clientAddress":"{{new string('a', 65)}}"}""", "application/json" },
        { "a form post", "clientAddress=203.0.113.7", "application/x-www-form-urlencoded" },
        { "plain text", "203.0.113.7", "text/plain" },
        { "no body at all", null, null },
    };

    // CONTROL: green before this change, for the same reason. Once the action is there, without
    // the marker the binder answers these: 400 to a body it cannot bind, 415 to one that is not JSON.
    [Theory]
    [MemberData(nameof(MalformedClaims))]
    public async Task WithTheDemoOff_AMalformedClaim_IsStill404(string what, string? body, string? mediaType)
    {
        using var client = _ordinary.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, ClaimPath);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, mediaType!);
        }

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "{0}: with the demo off the endpoint is not there, so nothing reads the body ({1})",
            what, await response.Content.ReadAsStringAsync());
    }
}
