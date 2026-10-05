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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The in-memory host has no body size limit. Its probe proves what the mint leaves unread;
/// the companion Kestrel tests prove the response and whether the connection survives.
/// </summary>
public sealed class MintOversizedBodyDrainTests(CustomWebApplicationFactory factory) : IntegrationTestBase(factory), IDisposable
{
    private WebApplicationFactory<Program>? _probed;

    public void Dispose() => _probed?.Dispose();

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
    [InlineData("/api/transfers/authorizations", 40_000)]
    [InlineData("/api/transfers/authorizations", 2_000_000)]
    [InlineData("/api/transfers/internal/authorizations", 40_000)]
    [InlineData("/api/transfers/internal/authorizations", 2_000_000)]
    [InlineData("/api/transactions/withdraw/authorizations", 40_000)]
    [InlineData("/api/transactions/withdraw/authorizations", 2_000_000)]
    [InlineData("/api/accounts/{id}/deletion-authorizations", 40_000)]
    [InlineData("/api/accounts/{id}/deletion-authorizations", 2_000_000)]
    public async Task MintOversizedBody_DrainsUpToTheCapAndClosesAboveIt(string path, int size)
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        var leftovers = new Leftovers();
        _probed = Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(leftovers);
            services.AddTransient<IStartupFilter, ReadWhatIsLeft>();
        }));
        using var client = _probed.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        path = path.Replace("{id}", accountId.ToString(), StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"pin":"123456"}""".PadRight(size), Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.PayloadTooLarge);
        if (size <= 1_048_576)
        {
            leftovers.ByContentLength[size].Should().Be(0, "the body must be discarded before the refusal");
            response.Headers.ConnectionClose.Should().NotBe(true, "the whole body was drained");
        }
        else
        {
            leftovers.ByContentLength[size].Should().BeGreaterThan(0, "above the cap the body is left unread");
            response.Headers.ConnectionClose.Should().BeTrue("an unread body makes the connection unusable");
        }
    }
}
