using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AzureBank.Api.Extensions;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Options;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What the API answers a caller that is NOT the BFF (ADR-0055). Every other integration test
/// speaks as the BFF does, because <see cref="CustomWebApplicationFactory"/> puts the service
/// credential on the clients it hands out; these take it off again.
/// </summary>
/// <remarks>
/// Measured on the running API on 2026-09-19, before the middleware, with no BFF in the path:
/// register 201, login 200 with a bearer token in the body, and with that token
/// <c>GET /api/accounts/{id}/full-number</c> 200 with the unmasked number, no PIN ever entered.
/// </remarks>
public class ServiceCredentialTests : IntegrationTestBase
{
    public ServiceCredentialTests(CustomWebApplicationFactory factory) : base(factory) { }

    private HttpClient ClientWith(string? key)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Remove(ServiceCredentialOptions.HeaderName);
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add(ServiceCredentialOptions.HeaderName, key);
        }

        return client;
    }

    private static async Task ShouldBeRefusedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.ServiceCredentialRequired);
        problem.GetProperty("status").GetInt32().Should().Be(401);
    }

    [Fact]
    public async Task ALoginThatDoesNotComeThroughTheBff_IsRefused_BeforeAnyTokenIsMinted()
    {
        using var direct = ClientWith(key: null);

        // Refused before the body is read, so whether these credentials are anyone's is not
        // something the answer can say: it is the same 401 as for every other path.
        var login = await direct.PostAsJsonAsync(
            "/api/auth/login", new { email = "someone@example.com", password = TestUserPassword });
        var register = await direct.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                azureTag = "direct" + Guid.NewGuid().ToString("N")[..8],
                email = $"direct-{Guid.NewGuid():N}@example.com",
                password = TestUserPassword,
                firstName = "Direct",
                lastName = "Caller",
            });

        await ShouldBeRefusedAsync(login);
        await ShouldBeRefusedAsync(register);
    }

    [Fact]
    public async Task AValidBearerToken_IsNotEnough_WithoutTheCredential()
    {
        var (token, _, accountId) = await RegisterTestUserAsync();

        using var direct = ClientWith(key: null);
        direct.DefaultRequestHeaders.Authorization = new("Bearer", token);

        await ShouldBeRefusedAsync(await direct.GetAsync("/api/accounts"));
        await ShouldBeRefusedAsync(await direct.GetAsync($"/api/accounts/{accountId}/full-number"));

        // The control: the same token WITH the credential is served, so the refusals above are
        // the credential's doing and not a broken token.
        SetAuthHeader(token);
        (await Client.GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("")]
    [InlineData("integration-tests-only-service-credential-0123456789abcdeX")]
    [InlineData("integration-tests-only-service-credential-0123456789abcdef-and-more")]
    [InlineData("INTEGRATION-TESTS-ONLY-SERVICE-CREDENTIAL-0123456789ABCDEF")]
    public async Task AWrongCredential_GetsTheSameAnswerAsNone(string wrong)
    {
        using var direct = ClientWith(wrong);

        await ShouldBeRefusedAsync(await direct.GetAsync("/api/accounts"));
    }

    [Fact]
    public async Task TheCredentialSentTwice_IsRefused_EvenWhenOneOfThemIsRight()
    {
        using var direct = ClientWith(key: null);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/accounts");
        request.Headers.TryAddWithoutValidation(
            ServiceCredentialOptions.HeaderName,
            [CustomWebApplicationFactory.ServiceCredentialKey, "a-second-value-smuggled-beside-it"]);

        await ShouldBeRefusedAsync(await direct.SendAsync(request));
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task TheHealthProbes_NeedNoCredential(string path)
    {
        using var direct = ClientWith(key: null);

        var response = await direct.GetAsync(path);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized, "an orchestrator's probe carries no key");
    }

    [Fact]
    public async Task TheApiDocument_IsNotExempt_OutsideDevelopment()
    {
        // The factory runs the host as "Testing". The exemption is for a developer's browser only.
        using var direct = ClientWith(key: null);

        await ShouldBeRefusedAsync(await direct.GetAsync("/openapi/v1.json"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("                                        ")]
    [InlineData("thirty-one-characters-long-key!")]
    public void TheRealRoot_RefusesToStart_WithoutAUsableKey(string? key)
    {
        // The DailyLimitOptionsTests idiom: the real composition root, resolved, because through
        // the host the refusal arrives as a disposed provider and its type cannot be asserted.
        // Every OTHER secret is supplied, so the one failure left is this key's.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Idempotency:HashKey"] = CustomWebApplicationFactory.IdempotencyHashKey,
            ["StepUp:BindingKey"] = CustomWebApplicationFactory.StepUpBindingKey,
            ["Security:PinPepper"] = CustomWebApplicationFactory.PinPepper,
            ["Audit:ChainKey"] = CustomWebApplicationFactory.AuditChainKey,
            ["Audit:AnchorKey"] = CustomWebApplicationFactory.AuditAnchorKey,
            ["ServiceCredential:BffKey"] = key,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices(configuration);
        using var root = services.BuildServiceProvider();

        var start = () => root.GetRequiredService<IStartupValidator>().Validate();

        start.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("ServiceCredential:BffKey must be configured");
    }

    [Fact]
    public void AKeyOfThirtyTwoCharacters_IsEnoughToStart()
    {
        ServiceCredentialOptions.IsUsable("thirty-two-characters-long-key!!").Should().BeTrue();
        ServiceCredentialOptions.MinimumKeyLength.Should().Be(32);
    }
}
