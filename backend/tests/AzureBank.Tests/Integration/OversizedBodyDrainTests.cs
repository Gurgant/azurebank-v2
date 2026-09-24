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
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

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
/// is left once the response is written. And the drain waits five seconds at most, on the host's
/// clock: a body that stops arriving is answered then, with a 413 that closes the connection.
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

    [Fact]
    public async Task ABodyThatStopsArriving_IsGivenUpOnAtFiveSeconds_AndThe413ClosesTheConnection()
    {
        var (token, _, _) = await RegisterTestUserAsync();
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
        // body is never whole -- the request would not leave.
        var client = _probed.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 40 KB of a declared 100 KB arrive, and the rest does not until the test lets it.
        var body = new StallingContent(declared: 100_000, sent: 40_000);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions/deposit") { Content = body };
        request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        try
        {
            var sending = client.SendAsync(request);

            // The drain is reading, so its deadline is set: a moment short of it, still no answer ...
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(1));
            await Task.Delay(200);
            sending.IsCompleted.Should().BeFalse("the drain waits five seconds for the rest of the body");

            // ... and at it, the 413 goes out without the rest.
            clock.Advance(TimeSpan.FromMilliseconds(1));
            var response = await sending.WaitAsync(TimeSpan.FromSeconds(10));

            await AssertTooLargeAsync(response);
            response.Headers.ConnectionClose.Should().BeTrue("the rest of that body may still be on its way");
        }
        finally
        {
            body.Release();
        }
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

    // A body that declares one length, sends the first part of it and then stops until released:
    // a sender that has stalled, or trickles, mid-body.
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

    private static async Task AssertTooLargeAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.IdempotencyPayloadTooLarge);
    }
}
