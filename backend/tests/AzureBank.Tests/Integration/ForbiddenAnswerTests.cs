using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What the API answers when authorisation itself refuses a signed-in caller: the 403 that the
/// JWT scheme's <c>OnForbidden</c> writes, in the problem shape, with <c>AUTH_FORBIDDEN</c>.
/// </summary>
/// <remarks>
/// <para>
/// No endpoint asks for a role or a policy, so no request reaches that answer on the host as it
/// is configured: every 403 the API sends today is thrown by a service and written by the
/// exception handler, with <c>ACCESS_DENIED</c>. The answer is kept for the day an endpoint does
/// ask for more than a valid token. Without it the scheme leaves the 403 empty and the
/// status-code pages fill it in: measured with the handler taken out, the same request is
/// answered <c>application/problem+json</c> with <c>type</c>, <c>title</c>, <c>status</c> and
/// <c>traceId</c>, and no <c>errorCode</c>. That is a refusal in a second shape, which the BFF
/// and the browser would have to tell apart from every other one.
/// </para>
/// <para>
/// The test makes the answer due on a derived host whose default policy asks for a role nobody
/// holds, so the framework's own authorisation step refuses an ordinary request for the accounts
/// list. The same token on the unchanged host is answered 200: the refusal comes from the policy
/// and from nothing else.
/// </para>
/// </remarks>
public sealed class ForbiddenAnswerTests : IntegrationTestBase
{
    public ForbiddenAnswerTests(CustomWebApplicationFactory factory) : base(factory)
    {
    }

    private static async Task<HttpResponseMessage> ListAccountsAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/accounts");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task ACallerThePolicyRefuses_Is403_AUTH_FORBIDDEN_InTheProblemShape()
    {
        // Measured 2026-10-13 on this host: 403, application/json; charset=utf-8, and the seven
        // members below.
        var (token, _, _) = await RegisterTestUserAsync();
        using var askingForARole = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Configure<AuthorizationOptions>(options =>
                    options.DefaultPolicy = new AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser()
                        .RequireRole("a-role-nobody-holds")
                        .Build())));
        using var client = askingForARole.CreateClient();

        var control = await ListAccountsAsync(Client, token);
        var response = await ListAccountsAsync(client, token);
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;

        using var all = new AssertionScope();
        control.StatusCode.Should().Be(HttpStatusCode.OK, "CONTROL: the token is good where no role is asked for");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.ToString().Should().Be("application/json; charset=utf-8");
        root.EnumerateObject().Select(member => member.Name).Should().Equal(
            "type", "title", "status", "detail", "instance", "errorCode", "traceId");
        root.GetProperty("type").GetString().Should().Be("https://httpstatuses.com/403");
        root.GetProperty("title").GetString().Should().Be("Forbidden");
        root.GetProperty("status").GetInt32().Should().Be(403);
        root.GetProperty("detail").GetString().Should().Be("You do not have permission to access this resource.");
        root.GetProperty("instance").GetString().Should().Be("/api/accounts");
        root.GetProperty("errorCode").GetString().Should().Be("AUTH_FORBIDDEN");
        root.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
