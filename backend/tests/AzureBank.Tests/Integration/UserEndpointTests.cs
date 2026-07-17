using System.Net;
using System.Net.Http.Json;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.User;
using AzureBank.Tests.Fixtures;
using FluentAssertions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Integration tests for recipient lookup (ADR-0014): exact-match only, authenticated,
/// masked name, validated input.
/// </summary>
public class UserEndpointTests : IntegrationTestBase
{
    public UserEndpointTests(CustomWebApplicationFactory factory) : base(factory) { }

    private async Task RegisterPayeeAsync(string azureTag)
    {
        var response = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = azureTag,
            Email = $"{azureTag}{Guid.NewGuid():N}@example.com",
            Password = "SecurePass123!",
            FirstName = "Vladislav",
            LastName = "Aleshaev"
        }, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static string Tag(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    [Fact]
    public async Task Lookup_ExactExistingTag_ReturnsMaskedName()
    {
        var tag = Tag("payee");
        await RegisterPayeeAsync(tag);
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var response = await Client.GetAsync($"/api/users/{tag}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<RecipientLookupResponse>>(JsonOptions);
        body!.Data!.Exists.Should().BeTrue();
        body.Data.DisplayName.Should().Be("Vladislav A."); // masked: first name + last initial
    }

    [Fact]
    public async Task Lookup_NonExistentTag_ReturnsNotExistsWithNoName()
    {
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var response = await Client.GetAsync($"/api/users/{Tag("ghost")}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<RecipientLookupResponse>>(JsonOptions);
        body!.Data!.Exists.Should().BeFalse();
        body.Data.DisplayName.Should().BeEmpty();
    }

    [Fact]
    public async Task Lookup_SubstringOfARealTag_DoesNotResolve()
    {
        // Exact-match only (ADR-0014): "smith" must not find "johnsmithNNNNNN".
        await RegisterPayeeAsync("johnsmith" + Guid.NewGuid().ToString("N")[..4]);
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        var response = await Client.GetAsync("/api/users/smith");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<RecipientLookupResponse>>(JsonOptions);
        body!.Data!.Exists.Should().BeFalse();
    }

    [Fact]
    public async Task Lookup_InvalidTag_ReturnsBadRequest()
    {
        var (token, _, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // Too short for [AzureTagQuery] (min 3) -> validated at the route parameter.
        var response = await Client.GetAsync("/api/users/ab");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Lookup_WithoutToken_ReturnsUnauthorized()
    {
        ClearAuthHeader();

        var response = await Client.GetAsync($"/api/users/{Tag("payee")}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
