using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Options;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Bff.Tests;

/// <summary>
/// Upstream error forwarding (ADR-0018): a JSON error body is forwarded verbatim with
/// its status, while a NON-JSON upstream body (proxy HTML, empty 500) must become a
/// generic 502 ProblemDetails — before BE-1 it escaped JsonDocument.Parse as an
/// unhandled exception and surfaced as a naked 500 with a stack-trace-shaped body.
/// </summary>
public class UpstreamErrorForwardingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public UpstreamErrorForwardingTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient ClientWithUpstream(HttpStatusCode status, string body, string mediaType)
    {
        return _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(() =>
                    new FakeBackendApiHandler(_ => new HttpResponseMessage(status)
                    {
                        Content = new StringContent(body, Encoding.UTF8, mediaType)
                    })))).CreateClient();
    }

    private static Task<HttpResponseMessage> PostLogin(HttpClient client) =>
        // Password must pass the BFF's own [Password] model validation so the request
        // actually reaches the (faked) upstream instead of 400ing at the controller.
        client.PostAsJsonAsync("/bff/auth/login", new { email = "err@example.com", password = "Password1!" });

    [Fact]
    public async Task JsonUpstreamError_IsForwardedVerbatim_WithItsStatus()
    {
        var upstreamBody = """
            {"type":"https://httpstatuses.com/401","title":"Unauthorized","status":401,
             "detail":"Invalid email or password","errorCode":"INVALID_CREDENTIALS","traceId":"deadbeef"}
            """;
        var client = ClientWithUpstream(HttpStatusCode.Unauthorized, upstreamBody, "application/problem+json");

        var response = await PostLogin(client);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("INVALID_CREDENTIALS");
        body.GetProperty("traceId").GetString().Should().Be("deadbeef");
    }

    [Fact]
    public async Task TheApiRefusingThisHostsKey_Becomes503WithRetryAfter_NotTheSignOut401()
    {
        // 06 §4.7 (F4). The API marks its refusal of the service key with a header; a key rotation
        // applied on one side is the service failing, and forwarded as its 401 it would sign the user
        // out. JsonUpstreamError_IsForwardedVerbatim_WithItsStatus is the control: a 401 WITHOUT the
        // header still passes through untouched.
        var client = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(() =>
                    new FakeBackendApiHandler(_ =>
                    {
                        var refusal = new HttpResponseMessage(HttpStatusCode.Unauthorized)
                        {
                            Content = new StringContent(
                                """{"status":401,"title":"Unauthorized","detail":"This API is reached through the BFF.","errorCode":"SERVICE_CREDENTIAL_REQUIRED"}""",
                                Encoding.UTF8, "application/problem+json")
                        };
                        refusal.Headers.Add(
                            ServiceCredentialOptions.RefusalHeaderName, ServiceCredentialOptions.ServiceCredentialRefusal);
                        return refusal;
                    })))).CreateClient();

        var response = await PostLogin(client);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.ServiceUnavailable);
        body.GetRawText().Should().NotContain("reached through the BFF", "the API's refusal text goes no further");
    }

    [Fact]
    public async Task HtmlUpstreamError_BecomesAGeneric502Problem_NotAnUnhandled500()
    {
        var client = ClientWithUpstream(
            HttpStatusCode.BadGateway, "<html><body>Bad Gateway</body></html>", "text/html");

        var response = await PostLogin(client);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("Bad Gateway");
        body.GetProperty("status").GetInt32().Should().Be(502);
    }

    [Fact]
    public async Task EmptyUpstream5xxBody_BecomesAGeneric502Problem()
    {
        var client = ClientWithUpstream(HttpStatusCode.InternalServerError, string.Empty, "application/json");

        var response = await PostLogin(client);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(502);
    }

    [Fact]
    public async Task EmptyUpstream4xxBody_PreservesTheClientStatus()
    {
        // The framework's bare 401/403/404 responses have EMPTY bodies by contract; the
        // status must survive as a ProblemDetails so status-based client flows
        // (session-expiry on 401) keep working — not degrade to 502.
        var client = ClientWithUpstream(HttpStatusCode.Unauthorized, string.Empty, "application/json");

        var response = await PostLogin(client);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(401);
    }
}
