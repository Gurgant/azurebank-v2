using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using static AzureBank.Bff.Tests.Polling;

namespace AzureBank.Bff.Tests;

/// <summary>
/// A revoke that fails in a way nobody listed costs its own grant and nothing else: the workers
/// that send revokes go on, and the next sign-out is still revoked at the API.
/// </summary>
/// <remarks>
/// <para>
/// The revoker is a hosted service with a fixed number of workers
/// (<see cref="GrantRevoker.Parallelism"/>). A send catches the network's failures and its own
/// timeout; anything else leaves it as an exception. Uncaught in the worker, that exception would
/// end the worker, and after as many such items as there are workers nothing would revoke a grant
/// again: "Esci" would go on answering 200 while every grant stayed live at the API until it
/// expired.
/// </para>
/// <para>
/// So the test fails MORE revokes than there are workers before it asks for a good one. With one
/// fewer, a regression would leave a worker standing and the test green.
/// </para>
/// </remarks>
public sealed class RevokeWorkersOutliveAFailureTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RevokeWorkersOutliveAFailureTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private sealed class ApiThatBreaksOnSomeGrants
    {
        private int _broken;

        public int Broken => Volatile.Read(ref _broken);

        public ConcurrentQueue<string> Revoked { get; } = new();

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            if (request.RequestUri!.AbsolutePath != "/api/auth/revoke")
            {
                return Ok();
            }

            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var grants = body.RootElement.GetProperty("refreshTokens").EnumerateArray()
                .Select(grant => grant.GetString()!).ToList();
            if (grants.Any(grant => grant.StartsWith("breaks-", StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref _broken);
                throw new InvalidOperationException("not a network failure and not a timeout");
            }

            foreach (var grant in grants)
            {
                Revoked.Enqueue(grant);
            }

            return Ok();
        }

        private static HttpResponseMessage Ok() =>
            new(HttpStatusCode.OK) { Content = new StringContent("""{"success":true}""", Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task AfterMoreUnexpectedFailuresThanThereAreWorkers_TheNextSignOutIsStillRevoked()
    {
        var api = new ApiThatBreaksOnSomeGrants();
        using var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(api.Respond))));
        var client = host.CreateClient();
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var cookieName = host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;

        async Task SignOutAsync(string grant)
        {
            var sessionId = sessions.CreateSession(
                "jwt-0",
                DateTime.UtcNow.AddHours(1),
                grant,
                DateTime.UtcNow.AddMinutes(60),
                new UserLoginInfo
                {
                    Id = Guid.NewGuid(),
                    AzureTag = "revoked",
                    Email = "revoked@example.com",
                    FirstName = "Re",
                    LastName = "Voked",
                    HasPin = true,
                });
            using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/auth/logout");
            request.Headers.Add("Cookie", $"{cookieName}={sessionId}");
            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var failures = GrantRevoker.Parallelism + 1;
        for (var i = 0; i < failures; i++)
        {
            await SignOutAsync($"breaks-{i}");
        }

        (await Eventually(() => api.Broken >= failures)).Should().BeTrue(
            $"ARRANGE: {failures} revokes failed unexpectedly, one more than there are workers (counted {api.Broken})");

        await SignOutAsync("good-grant");

        var revoked = await Eventually(() => api.Revoked.Contains("good-grant"));

        using var all = new AssertionScope();
        revoked.Should().BeTrue("a worker is still there to send it");
        api.Revoked.Should().Equal(["good-grant"], "once, and none of the grants whose revoke failed");
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested
            .Should().BeFalse("one bad item does not stop the host");
    }
}
