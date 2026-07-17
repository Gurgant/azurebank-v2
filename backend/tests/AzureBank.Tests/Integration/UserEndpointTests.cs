using System.Net;
using System.Net.Http.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.User;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    // Register a user with a known tag and return the auto-login token (ADR-0015 tests).
    private async Task<string> RegisterReturningTokenAsync(string azureTag)
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
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(JsonOptions);
        return body!.Data!.Token.AccessToken;
    }

    [Fact]
    public async Task Register_SetsUserNameToImmutableUuidV7Id_NotAzureTag()
    {
        // Decouple (ADR-0015): Identity's UserName is the user id (UUIDv7), not the handle.
        var tag = Tag("decoup");
        await RegisterPayeeAsync(tag);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var user = await db.Users.FirstAsync(u => u.AzureTag == tag);

        user.UserName.Should().Be(user.Id.ToString());
        user.UserName.Should().NotBe(tag);
        user.Id.Version.Should().Be(7); // UUIDv7 (time-sortable)
    }

    [Fact]
    public async Task Rename_UpdatesHandle_OldTagStopsResolving()
    {
        var oldTag = Tag("old");
        var token = await RegisterReturningTokenAsync(oldTag);
        SetAuthHeader(token);
        var newTag = Tag("new");

        var response = await Client.PatchAsJsonAsync(
            "/api/users/me/azuretag", new UpdateAzureTagRequest { AzureTag = newTag }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<UpdateAzureTagResponse>>(JsonOptions);
        body!.Data!.AzureTag.Should().Be(newTag);

        // A different signed-in user confirms the new handle resolves and the old one doesn't.
        SetAuthHeader(await RegisterReturningTokenAsync(Tag("obs")));
        (await (await Client.GetAsync($"/api/users/{newTag}"))
            .Content.ReadFromJsonAsync<ApiResponse<RecipientLookupResponse>>(JsonOptions))!.Data!.Exists
            .Should().BeTrue();
        (await (await Client.GetAsync($"/api/users/{oldTag}"))
            .Content.ReadFromJsonAsync<ApiResponse<RecipientLookupResponse>>(JsonOptions))!.Data!.Exists
            .Should().BeFalse();
    }

    [Fact]
    public async Task Rename_ToTakenTag_ReturnsConflict()
    {
        var takenTag = Tag("taken");
        await RegisterPayeeAsync(takenTag); // held by someone else
        SetAuthHeader(await RegisterReturningTokenAsync(Tag("me")));

        var response = await Client.PatchAsJsonAsync(
            "/api/users/me/azuretag", new UpdateAzureTagRequest { AzureTag = takenTag }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Rename_InvalidTag_ReturnsBadRequest()
    {
        SetAuthHeader(await RegisterReturningTokenAsync(Tag("val")));

        var response = await Client.PatchAsJsonAsync(
            "/api/users/me/azuretag", new UpdateAzureTagRequest { AzureTag = "ab" }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rename_WithoutToken_ReturnsUnauthorized()
    {
        ClearAuthHeader();

        var response = await Client.PatchAsJsonAsync(
            "/api/users/me/azuretag", new UpdateAzureTagRequest { AzureTag = Tag("nope") }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
