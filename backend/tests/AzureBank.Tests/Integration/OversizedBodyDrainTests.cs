using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AzureBank.Shared.Constants;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// An oversized body on an idempotent endpoint is refused with 413 only after it has been read to
/// its end, up to 1 MiB, so the refusal does not close the connection under a sender that is still
/// writing it (backlog row 42). Refused unread, the body was left for Kestrel, which aborted the
/// connection at the endpoint's 32 KB limit; through the BFF that surfaced as a reset, a 502, or a
/// 502 for the NEXT request on a pooled connection — 3 of 42 runs of the contract suite's
/// oversized-body test, measured 2026-09-24 on the running stack, and 0 of 30 with the body drained.
/// In memory there is no socket to reset, so what these assert is the cause itself: whether the
/// app left any of the body unread when it answered. A probe around the whole pipeline reads what
/// is left once the response is written.
/// </summary>
public class OversizedBodyDrainTests : IntegrationTestBase, IDisposable
{
    private WebApplicationFactory<Program>? _probed;

    public OversizedBodyDrainTests(CustomWebApplicationFactory factory) : base(factory) { }

    public void Dispose()
    {
        _probed?.Dispose();
        GC.SuppressFinalize(this);
    }

    public sealed class Leftovers
    {
        public ConcurrentDictionary<long, int> ByContentLength { get; } = new();
    }

    private sealed class ReadWhatIsLeft(Leftovers leftovers) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, pipeline) =>
            {
                await pipeline();
                if (context.Request.ContentLength is { } length)
                {
                    var buffer = new byte[8192];
                    leftovers.ByContentLength[length] = await context.Request.Body.ReadAsync(buffer);
                }
            });
            next(app);
        };
    }

    [Theory]
    [InlineData(40_000)]
    [InlineData(512_000)]
    public async Task AnOversizedBodyUpToTheCap_IsReadToItsEnd_BeforeThe413(int padding)
    {
        var (response, contentLength, leftovers) = await PostOversizedDepositAsync(padding);

        await AssertTooLargeAsync(response);
        leftovers.ByContentLength[contentLength].Should().Be(0, "the body was read to its end before the 413");
        response.Headers.ConnectionClose.Should().NotBe(true, "a drained connection can be kept alive");
    }

    [Fact]
    public async Task ABodyAboveTheCap_IsNotRead_AndThe413ClosesTheConnection()
    {
        var (response, contentLength, leftovers) = await PostOversizedDepositAsync(2_000_000);

        await AssertTooLargeAsync(response);
        leftovers.ByContentLength[contentLength].Should().BeGreaterThan(0, "above 1 MiB nothing is read");
        response.Headers.ConnectionClose.Should().BeTrue("no proxy may reuse a connection about to go");
    }

    private async Task<(HttpResponseMessage Response, long ContentLength, Leftovers Leftovers)>
        PostOversizedDepositAsync(int padding)
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        var leftovers = new Leftovers();
        _probed = Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(leftovers);
            services.AddTransient<IStartupFilter, ReadWhatIsLeft>();
        }));
        var client = _probed.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var json = JsonSerializer.Serialize(new
        {
            accountId,
            amount = 10m,
            description = new string('x', padding)
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);
        return (response, Encoding.UTF8.GetByteCount(json), leftovers);
    }

    private static async Task AssertTooLargeAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.IdempotencyPayloadTooLarge);
    }
}
