using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Two inputs the API refuses before a controller runs, and what it says: an enum that is not
/// written as one of its names, and a handle in a route that cannot be a handle.
/// </summary>
/// <remarks>
/// <para>
/// THE ENUM. The API reads an enum from its NAME and refuses the number: a client that sends
/// <c>"type": 1</c> has serialised with the defaults of <c>System.Net.Http.Json</c>, and an
/// accepted integer would bind to whatever member holds that position today. The answer names
/// the valid values, which is what makes the refusal usable.
/// </para>
/// <para>
/// THE HANDLE. <c>GET /api/users/{azureTag}</c> is an exact-match confirmation and answers 200
/// with <c>exists: false</c> for a handle nobody holds (ADR-0014). A string that cannot be a
/// handle at all is not asked of the store: it is a 400 that says which rule it broke.
/// </para>
/// <para>
/// Both answers are the framework's validation problem, written by model binding: no
/// <c>errorCode</c>, and the reasons under <c>errors</c>. The body is asserted as it was
/// measured against the running host on 2026-10-12.
/// </para>
/// </remarks>
public sealed class EnumAndHandleInputTests : IntegrationTestBase
{
    public EnumAndHandleInputTests(CustomWebApplicationFactory factory) : base(factory)
    {
    }

    private async Task<HttpResponseMessage> OpenAccountAsync(string token, string typeAsJson)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/accounts")
        {
            Content = new StringContent(
                $$"""{"name":"Holiday fund","type":{{typeAsJson}}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await Client.SendAsync(request);
    }

    private async Task<int> CountAccountsAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/accounts");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetArrayLength();
    }

    [Theory]
    [InlineData("1", "Integer values are not allowed for enum 'AccountType'. Use string values: Checking, Savings, Investment")]
    [InlineData("true", "Expected string value for enum 'AccountType', but got True.")]
    [InlineData("null", "Expected string value for enum 'AccountType', but got Null.")]
    [InlineData("\"\"", "Empty string is not a valid value for enum 'AccountType'.")]
    [InlineData("\"Bogus\"", "'Bogus' is not a valid value for enum 'AccountType'. Valid values: Checking, Savings, Investment")]
    public async Task AnAccountTypeThatIsNotOneOfItsNames_Is400_SaysWhy_AndOpensNoAccount(string typeAsJson, string reason)
    {
        var (token, _, _) = await RegisterTestUserAsync();
        var before = await CountAccountsAsync(token);

        var response = await OpenAccountAsync(token, typeAsJson);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("errors").GetProperty("$.type").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(reason);
        (await CountAccountsAsync(token)).Should().Be(before, "a refused request opens nothing");
    }

    [Fact]
    public async Task AnAccountTypeNamedInAnotherCase_IsTaken_AndTheAnswerSpellsItAsTheContractDoes()
    {
        // The name is matched without regard to case; what comes back is the member's own spelling.
        var (token, _, _) = await RegisterTestUserAsync();

        var response = await OpenAccountAsync(token, "\"savings\"");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        body.GetProperty("data").GetProperty("type").GetString().Should().Be("Savings");
    }

    [Theory]
    [InlineData("ab", "AzureTag must be at least 3 characters.")]
    [InlineData("this_handle_is_longer_than_twenty", "AzureTag cannot exceed 20 characters.")]
    [InlineData("Has_Capitals", "AzureTag must start with a letter and contain only lowercase letters, numbers, and underscores.")]
    [InlineData("9starts_with_digit", "AzureTag must start with a letter and contain only lowercase letters, numbers, and underscores.")]
    public async Task AStringThatCannotBeAHandle_Is400_WithTheRuleItBroke(string handle, string rule)
    {
        var (token, _, _) = await RegisterTestUserAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/users/{handle}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("errors").GetProperty("azureTag").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(rule);
        body.TryGetProperty("data", out _).Should().BeFalse("nothing was looked up");
    }

    [Fact]
    public async Task AWellFormedHandleNobodyHolds_Is200_ExistsFalse_NotARefusal()
    {
        // The other side of the line the theory above draws: this one IS asked of the store.
        var (token, _, _) = await RegisterTestUserAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/nobody_has_this");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        using var all = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("data").GetProperty("exists").GetBoolean().Should().BeFalse();
        body.GetProperty("data").GetProperty("displayName").GetString().Should().BeEmpty();
    }
}
