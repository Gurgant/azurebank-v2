using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AzureBank.Api.Attributes;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Exceptions;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The in-memory host has no body size limit. Its probe proves what the mint leaves unread;
/// the companion Kestrel tests prove the response and whether the connection survives.
/// Two rules of the mints' filter that no request on Kestrel shows are held here as well: the
/// limit is the endpoint's own, and the drain's five seconds run on the host's clock.
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

    // Control: the filter refuses above the limit the ENDPOINT declares, whatever it is, and not
    // above the 32,768 bytes the four mints happen to share. The filter is run by hand on an
    // endpoint that declares 1,000 bytes. Its proof is a filter that compares with a constant
    // 32,768: the 1,001 row is then let through.
    [Theory]
    [InlineData(1_000, false)]
    [InlineData(1_001, true)]
    public async Task TheFilter_RefusesAboveTheEndpointsOwnLimit(long contentLength, bool refused)
    {
        using var services = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.ContentLength = contentLength;
        http.Request.Body = new MemoryStream(new byte[contentLength]);
        http.SetEndpoint(new Endpoint(
            requestDelegate: null,
            new EndpointMetadataCollection(new RequestSizeLimitAttribute(1_000)),
            displayName: "an endpoint that declares 1,000 bytes"));
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var executing = new ResourceExecutingContext(action, [], []);
        var ran = false;
        var act = () => new RefuseOversizedBodyAttribute().OnResourceExecutionAsync(executing, () =>
        {
            ran = true;
            return Task.FromResult(new ResourceExecutedContext(action, []));
        });

        if (refused)
        {
            await act.Should().ThrowAsync<PayloadTooLargeException>();
            ran.Should().BeFalse("a refused request does not reach the action");
        }
        else
        {
            await act.Should().NotThrowAsync();
            ran.Should().BeTrue("a body at the limit is the action's to answer");
        }
    }

    // The five seconds the drain waits at a mint run on the host's clock, the one a test replaces.
    // With that clock stopped, a body that stops arriving is still waited for after six real
    // seconds; the 413 goes out when the clock reaches five. Its proof is the filter handing the
    // drain the system clock: the answer is then there before the six seconds are up.
    [Fact]
    public async Task MintBodyThatStopsArriving_IsGivenUpOnAtFiveSecondsOfTheHostsClock()
    {
        var (token, _, accountId) = await RegisterTestUserAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _probed = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
                services.AddTransient<IStartupFilter>(_ => new ReportFirstRead(reading)));
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        });
        // No redirect handler: it copies a request's whole body before sending any of it, and this
        // body is never whole.
        using var client = _probed.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 40,000 bytes of a declared 100,000 arrive, and the rest does not until the test lets it.
        var body = new StallingContent(declared: 100_000, sent: 40_000);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/accounts/{accountId}/deletion-authorizations")
        {
            Content = body,
        };
        try
        {
            var sending = client.SendAsync(request);

            // The drain is reading, so its deadline is set. Six real seconds on, the host's clock
            // has not moved and there is no answer.
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromSeconds(6));
            sending.IsCompleted.Should().BeFalse("the five seconds run on the host's clock, which has not moved");

            // A moment short of five seconds on that clock, still none ...
            clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(1));
            await Task.Delay(200);
            sending.IsCompleted.Should().BeFalse("the drain waits five seconds for the rest of the body");

            // ... and at five, the 413 goes out without the rest.
            clock.Advance(TimeSpan.FromMilliseconds(1));
            using var response = await sending.WaitAsync(TimeSpan.FromSeconds(10));

            response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            problem.RootElement.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.PayloadTooLarge);
            response.Headers.ConnectionClose.Should().BeTrue("the rest of that body may still be on its way");
        }
        finally
        {
            body.Release();
        }
    }

    // Swaps the request body for one that reports its first read: the drain's, which starts only
    // once its deadline is set, so the test moves the clock no sooner than that.
    private sealed class ReportFirstRead(TaskCompletionSource reading) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, pipeline) =>
            {
                context.Request.Body = new ReportingStream(context.Request.Body, reading);
                return pipeline();
            });
            next(app);
        };
    }

    private sealed class ReportingStream(Stream inner, TaskCompletionSource reading) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult();
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A body that declares one length, sends the first part of it and then stops until released.
    private sealed class StallingContent(int declared, int sent) : HttpContent
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _released.TrySetResult();

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(new byte[sent]);
            await stream.FlushAsync();
            await _released.Task;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = declared;
            return true;
        }
    }
}
