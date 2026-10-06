using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Serilog.Events;
using Xunit.Abstractions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Real Kestrel proofs: routing must set the monetary body limit before authentication, and MVC
/// must not try to set it again after idempotency has read the body. TestServer has no size feature.
/// </summary>
public sealed class KestrelRequestSizeLimitTests : IDisposable
{
    private const string FilterSource = "Microsoft.AspNetCore.Mvc.Filters.RequestSizeLimitFilter";
    private const string RoutingSource = "Microsoft.AspNetCore.Routing.EndpointRoutingMiddleware";
    private const string AuthenticationSource = "Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler";
    private const string HandlerSource = "AzureBank.Api.Handlers.AppExceptionHandler";
    private const string DepositPath = "/api/transactions/deposit";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly DebugLogFactory _factory = new();
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public KestrelRequestSizeLimitTests(ITestOutputHelper output)
    {
        _output = output;
        _factory.CaptureLog(LogEventLevel.Debug);
        _factory.UseKestrel();
        _factory.StartServer();
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task MoneyRequests_DoNotReapplyTheLimitAfterIdempotencyReadsTheBody()
    {
        var (token, accountId) = await RegisterAsync();
        var requests = new[]
        {
            (DepositPath, HttpStatusCode.Created, (string?)null),
            ("/api/transactions/withdraw", HttpStatusCode.Unauthorized, ErrorCodes.AuthorizationRequired),
            ("/api/transfers", HttpStatusCode.Unauthorized, ErrorCodes.AuthorizationRequired),
            ("/api/transfers/internal", HttpStatusCode.NotFound, ErrorCodes.AccountNotFound),
        };

        foreach (var (path, status, errorCode) in requests)
        {
            using var response = await SendAsync(path, MoneyBody(path, accountId), token);
            response.StatusCode.Should().Be(status);
            if (errorCode is not null)
            {
                await AssertErrorCodeAsync(response, errorCode);
            }
        }

        // Positive control in this very host: absence of the MVC event only counts if the sink
        // is listening. The oversized refusal must still be logged at Warning by the handler.
        using var oversized = await SendAsync(DepositPath, OversizedBody(accountId), token);
        oversized.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        await AssertErrorCodeAsync(oversized, ErrorCodes.IdempotencyPayloadTooLarge);
        _factory.CapturedEvents.Should().Contain(e =>
            IsSource(e, HandlerSource)
            && e.Level == LogEventLevel.Warning
            && e.Properties["ErrorCode"].Equals(new ScalarValue(ErrorCodes.IdempotencyPayloadTooLarge)));

        var filterEvents = _factory.CapturedEvents.Where(e => IsSource(e, FilterSource)).ToList();
        foreach (var e in filterEvents)
        {
            _output.WriteLine($"{e.Level} {e.Properties["EventId"]}: {e.RenderMessage()}");
        }

        var messages = filterEvents.Select(e => $"{e.Level}: {e.RenderMessage()}").ToList();
        messages.Should().BeEmpty(
            "routing must apply the limit before idempotency reads the body, without MVC applying it again");
    }

    // Controls: both lengths are refused identically before and after the metadata change.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Control_OversizedDeposit_Is413WithTheSameCode(bool chunked)
    {
        var (token, accountId) = await RegisterAsync();
        using var response = await SendAsync(DepositPath, OversizedBody(accountId), token, chunked);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        await AssertErrorCodeAsync(response, ErrorCodes.IdempotencyPayloadTooLarge);
    }

    [Fact]
    public async Task Control_InLimitChunkedDeposit_Is201()
    {
        var (token, accountId) = await RegisterAsync();
        using var response = await SendAsync(DepositPath, MoneyBody(DepositPath, accountId), token, chunked: true);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // Controls: the four mints do not read through idempotency, so their MVC filter can still set
    // the feature. Its Debug event also proves that this category has not been silenced.
    [Theory]
    [InlineData("/api/transactions/withdraw/authorizations", HttpStatusCode.BadRequest)]
    [InlineData("/api/transfers/authorizations", HttpStatusCode.BadRequest)]
    [InlineData("/api/transfers/internal/authorizations", HttpStatusCode.BadRequest)]
    [InlineData("/api/accounts/{id}/deletion-authorizations", HttpStatusCode.UnprocessableEntity)]
    public async Task Control_MintFilter_SetsTheLimitWithoutAWarning(string path, HttpStatusCode status)
    {
        var (token, accountId) = await RegisterAsync();
        path = path.Replace("{id}", accountId.ToString(), StringComparison.Ordinal);
        using var response = await SendAsync(path, """{"pin":"123456"}""", token, idempotent: false);

        response.StatusCode.Should().Be(status);
        var events = _factory.CapturedEvents.Where(e => IsSource(e, FilterSource)).ToList();
        events.Should().NotContain(e => e.Level >= LogEventLevel.Warning);
        var applied = events.Where(e => HasEvent(e, 3, "MaxRequestBodySizeSet")).Should().ContainSingle().Subject;
        applied.Level.Should().Be(LogEventLevel.Debug);
        applied.Properties["RequestSize"].Should().Be(new ScalarValue("32768"));
    }

    // Controls: removing endpoint metadata or setting the limit only inside idempotency would
    // fail these. Without a token the request never reaches idempotency, but routing still caps it.
    [Theory]
    [InlineData("/api/transactions/deposit")]
    [InlineData("/api/transactions/withdraw")]
    [InlineData("/api/transfers")]
    [InlineData("/api/transfers/internal")]
    public async Task Control_UnauthenticatedMoneyRequest_Sets32768OnceBeforeAuthentication(string path)
    {
        using var response = await SendAsync(path, MoneyBody(path, Guid.NewGuid()), token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var events = _factory.CapturedEvents.ToList();
        var applied = events.Where(e => IsSource(e, RoutingSource) && HasEvent(e, 11, "MaxRequestBodySizeSet"))
            .Should().ContainSingle().Subject;
        applied.Level.Should().Be(LogEventLevel.Debug);
        applied.Properties["RequestSize"].Should().Be(new ScalarValue("32768"));

        var authenticationIndex = events.FindIndex(e => IsSource(e, AuthenticationSource));
        authenticationIndex.Should().BeGreaterThanOrEqualTo(0, "the authentication log must be captured too");
        events.IndexOf(applied).Should().BeLessThan(authenticationIndex);
    }

    [Theory]
    [InlineData("/api/transfers/authorizations", 40_000, false)]
    [InlineData("/api/transfers/authorizations", 40_000, true)]
    [InlineData("/api/transfers/authorizations", 32_769, false)]
    [InlineData("/api/transfers/authorizations", 32_756, true)]
    [InlineData("/api/transfers/internal/authorizations", 40_000, false)]
    [InlineData("/api/transfers/internal/authorizations", 40_000, true)]
    [InlineData("/api/transfers/internal/authorizations", 32_769, false)]
    [InlineData("/api/transfers/internal/authorizations", 32_756, true)]
    [InlineData("/api/transactions/withdraw/authorizations", 40_000, false)]
    [InlineData("/api/transactions/withdraw/authorizations", 40_000, true)]
    [InlineData("/api/transactions/withdraw/authorizations", 32_769, false)]
    [InlineData("/api/transactions/withdraw/authorizations", 32_756, true)]
    [InlineData("/api/accounts/{id}/deletion-authorizations", 40_000, false)]
    [InlineData("/api/accounts/{id}/deletion-authorizations", 40_000, true)]
    [InlineData("/api/accounts/{id}/deletion-authorizations", 32_769, false)]
    [InlineData("/api/accounts/{id}/deletion-authorizations", 32_756, true)]
    public async Task MintOversizedBody_Is413WithItsCodeAndConnectionPolicy(string path, int size, bool chunked)
    {
        var (token, accountId) = await RegisterAsync();
        var body = MintBody(path, accountId).PadRight(size);
        path = path.Replace("{id}", accountId.ToString(), StringComparison.Ordinal);
        using var response = await SendAsync(path, body, token, chunked, idempotent: false);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (response.Content.Headers.ContentType?.MediaType).Should().Be("application/json");
        var json = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"{(chunked ? "chunked" : "Content-Length")} {size}: {json}");
        AssertMintTooLarge(json, path);
        if (chunked)
        {
            response.Headers.ConnectionClose.Should().BeTrue("the chunked body is left unread");
        }
        else
        {
            response.Headers.ConnectionClose.Should().NotBe(true, "a drained connection can be kept alive");
        }
    }

    // Controls, green before and after the mints' 413: the largest body a mint still takes gets its
    // usual answer, and 40,000 bytes with no token get the 401 first. With a Content-Length that body
    // is 32,768 bytes; in one chunk it is 32,755, because the server counts the chunk's 13 bytes of
    // framing as well (the theory above has 32,756 in one chunk refused). The request with no token
    // is the last this test sends: Kestrel can abort the connection after its keep-alive 401.
    [Theory]
    [InlineData("/api/transfers/authorizations", false)]
    [InlineData("/api/transfers/authorizations", true)]
    [InlineData("/api/transfers/internal/authorizations", false)]
    [InlineData("/api/transfers/internal/authorizations", true)]
    [InlineData("/api/transactions/withdraw/authorizations", false)]
    [InlineData("/api/transactions/withdraw/authorizations", true)]
    [InlineData("/api/accounts/{id}/deletion-authorizations", false)]
    [InlineData("/api/accounts/{id}/deletion-authorizations", true)]
    public async Task Control_MintBoundaryAndMissingToken_KeepTheirAnswers(string path, bool chunked)
    {
        var (token, accountId) = await RegisterAsync();
        var body = MintBody(path, accountId);
        var expected = MintUsualAnswer(path);
        path = path.Replace("{id}", accountId.ToString(), StringComparison.Ordinal);
        using var boundary = await SendAsync(path, body.PadRight(chunked ? 32_755 : 32_768),
            token, chunked, idempotent: false);
        boundary.StatusCode.Should().Be(expected.Status);
        await AssertErrorCodeAsync(boundary, expected.Code);

        var unauthenticatedBody = Encoding.UTF8.GetBytes(body.PadRight(40_000));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, _client.BaseAddress!.Port, deadline.Token);
        await using var stream = socket.GetStream();
        await stream.WriteAsync(
            MintRequestHead(path, token: null, unauthenticatedBody.Length, chunked: chunked), deadline.Token);

        if (chunked)
        {
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes($"{16_000:x}\r\n"), deadline.Token);
            await stream.WriteAsync(unauthenticatedBody.AsMemory(0, 16_000), deadline.Token);
            await stream.WriteAsync("\r\n"u8.ToArray(), deadline.Token);
        }
        else
        {
            await stream.WriteAsync(unauthenticatedBody.AsMemory(0, 16_000), deadline.Token);
        }

        var unauthenticated = await ReadWireResponseAsync(stream, deadline.Token);

        try
        {
            if (chunked)
            {
                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes($"{24_000:x}\r\n"), deadline.Token);
                await stream.WriteAsync(unauthenticatedBody.AsMemory(16_000), deadline.Token);
                await stream.WriteAsync("\r\n0\r\n\r\n"u8.ToArray(), deadline.Token);
            }
            else
            {
                await stream.WriteAsync(unauthenticatedBody.AsMemory(16_000), deadline.Token);
            }
            await stream.FlushAsync(deadline.Token);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
        }

        unauthenticated.Status.Should().Be((int)HttpStatusCode.Unauthorized);
        using var unauthenticatedProblem = JsonDocument.Parse(unauthenticated.Body);
        unauthenticatedProblem.RootElement.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.TokenMissing);
    }

    [Theory]
    [InlineData("/api/transfers/authorizations")]
    [InlineData("/api/transfers/internal/authorizations")]
    [InlineData("/api/transactions/withdraw/authorizations")]
    [InlineData("/api/accounts/{id}/deletion-authorizations")]
    public async Task MintSplitBody_Delivers413AndThenTheUsualAnswerOnTheSameConnection(string path)
    {
        var (token, accountId) = await RegisterAsync();
        var small = MintBody(path, accountId);
        var expected = MintUsualAnswer(path);
        path = path.Replace("{id}", accountId.ToString(), StringComparison.Ordinal);
        var body = Encoding.UTF8.GetBytes(small.PadRight(40_000));

        for (var round = 0; round < 5; round++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var socket = new System.Net.Sockets.TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, _client.BaseAddress!.Port, deadline.Token);
            await using var stream = socket.GetStream();
            WireResponse? first = null;
            WireResponse? second = null;
            string? transportError = null;
            try
            {
                await stream.WriteAsync(MintRequestHead(path, token, body.Length), deadline.Token);
                await stream.WriteAsync(body.AsMemory(0, 16_000), deadline.Token);
                await Task.Delay(300, deadline.Token);
                await stream.WriteAsync(body.AsMemory(16_000), deadline.Token);
                first = await ReadWireResponseAsync(stream, deadline.Token);
                await stream.WriteAsync(MintRequestHead(path, token, Encoding.UTF8.GetByteCount(small)), deadline.Token);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(small), deadline.Token);
                second = await ReadWireResponseAsync(stream, deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
            {
                transportError = $"{ex.GetType().Name}: {ex.Message}";
            }

            transportError.Should().BeNull($"round {round + 1} must deliver both answers, including while the sender writes");
            first.Should().NotBeNull();
            first!.Status.Should().Be(413);
            first.Headers["Content-Type"].Should().StartWith("application/json");
            first.Headers.GetValueOrDefault("Connection").Should().NotBe("close");
            AssertMintTooLarge(first.Body, path);
            second.Should().NotBeNull();
            second!.Status.Should().Be((int)expected.Status);
            using var usual = JsonDocument.Parse(second.Body);
            usual.RootElement.GetProperty("errorCode").GetString().Should().Be(expected.Code);
            _output.WriteLine($"Round {round + 1}: 413 then {second.Status} on the same TCP connection.");
        }
    }

    // Control, green before and after the mints' 413: a body the server refuses for another reason
    // than its size keeps the answer it had. A chunk whose size line is no number is the server's
    // 400, not its 413, so the mint's filter leaves it to the 400 every such request gets. Its proof
    // is the filter turning ANY refusal of the server's into the 413: these four rows then read 413.
    [Theory]
    [InlineData("/api/transfers/authorizations")]
    [InlineData("/api/transfers/internal/authorizations")]
    [InlineData("/api/transactions/withdraw/authorizations")]
    [InlineData("/api/accounts/{id}/deletion-authorizations")]
    public async Task Control_MintBadChunkSize_KeepsTheMalformed400(string path)
    {
        var (token, accountId) = await RegisterAsync();
        path = path.Replace("{id}", accountId.ToString(), StringComparison.Ordinal);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, _client.BaseAddress!.Port, deadline.Token);
        await using var stream = socket.GetStream();
        var head = new StringBuilder($"POST {path} HTTP/1.1\r\nHost: {_client.BaseAddress!.Authority}\r\n");
        foreach (var header in _client.DefaultRequestHeaders)
        {
            head.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append("\r\n");
        }
        head.Append("Authorization: Bearer ").Append(token).Append("\r\n")
            .Append("Content-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n")
            .Append("zz\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), deadline.Token);
        var response = await ReadWireResponseAsync(stream, deadline.Token);

        _output.WriteLine($"{response.Status}: {response.Body}");
        response.Status.Should().Be(400, "the server refused the chunk's framing, not the body's size");
        using var problem = JsonDocument.Parse(response.Body);
        problem.RootElement.GetProperty("detail").GetString()
            .Should().Be("The request is malformed or contains invalid characters.");
        problem.RootElement.TryGetProperty("errorCode", out _).Should().BeFalse("that 400 names no code");
    }

    private static string MintBody(string path, Guid accountId)
    {
        object body = path switch
        {
            "/api/transfers/authorizations" =>
                new { fromAccountId = accountId, recipientAzureTag = "missing_payee", amount = 1m, pin = "123456" },
            "/api/transfers/internal/authorizations" =>
                new { fromAccountId = accountId, toAccountId = Guid.NewGuid(), amount = 1m, pin = "123456" },
            "/api/transactions/withdraw/authorizations" => new { accountId, amount = 1m, pin = "123456" },
            "/api/accounts/{id}/deletion-authorizations" => new { pin = "123456" },
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
        return JsonSerializer.Serialize(body, Json);
    }

    private static (HttpStatusCode Status, string Code) MintUsualAnswer(string path) => path switch
    {
        "/api/transfers/authorizations" or "/api/transfers/internal/authorizations" =>
            (HttpStatusCode.NotFound, ErrorCodes.AccountNotFound),
        "/api/transactions/withdraw/authorizations" => (HttpStatusCode.UnprocessableEntity, ErrorCodes.PinRequired),
        "/api/accounts/{id}/deletion-authorizations" => (HttpStatusCode.UnprocessableEntity, ErrorCodes.PrimaryAccountDelete),
        _ => throw new ArgumentOutOfRangeException(nameof(path)),
    };

    private static void AssertMintTooLarge(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        var problem = document.RootElement;
        problem.GetProperty("type").GetString().Should().Be("https://httpstatuses.com/413");
        problem.GetProperty("title").GetString().Should().Be("Payload Too Large");
        problem.GetProperty("status").GetInt32().Should().Be(413);
        problem.GetProperty("detail").GetString().Should().Be("The request body exceeds the 32 KB limit for this endpoint.");
        problem.GetProperty("instance").GetString().Should().Be(path);
        problem.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.PayloadTooLarge);
        problem.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private byte[] MintRequestHead(
        string path, string? token, int? length = null, bool chunked = false)
    {
        var head = new StringBuilder($"POST {path} HTTP/1.1\r\nHost: {_client.BaseAddress!.Authority}\r\n");
        foreach (var header in _client.DefaultRequestHeaders)
        {
            head.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append("\r\n");
        }
        if (token is not null)
        {
            head.Append("Authorization: Bearer ").Append(token).Append("\r\n");
        }
        head.Append("Content-Type: application/json\r\n");
        if (chunked)
        {
            head.Append("Transfer-Encoding: chunked\r\n\r\n");
        }
        else
        {
            head.Append("Content-Length: ").Append(length).Append("\r\n\r\n");
        }
        return Encoding.ASCII.GetBytes(head.ToString());
    }

    private sealed record WireResponse(int Status, Dictionary<string, string> Headers, string Body);

    private static async Task<WireResponse> ReadWireResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var status = await ReadWireLineAsync(stream, cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string line;
        while ((line = await ReadWireLineAsync(stream, cancellationToken)).Length != 0)
        {
            var colon = line.IndexOf(':');
            headers.Add(line[..colon], line[(colon + 1)..].Trim());
        }

        using var body = new MemoryStream();
        if (headers.TryGetValue("Transfer-Encoding", out var transfer) && transfer == "chunked")
        {
            while (true)
            {
                var sizeLine = await ReadWireLineAsync(stream, cancellationToken);
                var size = Convert.ToInt32(sizeLine.Split(';')[0], 16);
                if (size == 0)
                {
                    while ((await ReadWireLineAsync(stream, cancellationToken)).Length != 0)
                    {
                    }
                    break;
                }
                var chunk = new byte[size];
                await stream.ReadExactlyAsync(chunk, cancellationToken);
                body.Write(chunk);
                (await ReadWireLineAsync(stream, cancellationToken)).Should().BeEmpty();
            }
        }
        else
        {
            headers.Should().ContainKey("Content-Length", "both responses must have explicit HTTP/1.1 framing");
            var bytes = new byte[int.Parse(headers["Content-Length"], System.Globalization.CultureInfo.InvariantCulture)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            body.Write(bytes);
        }
        return new WireResponse(int.Parse(status.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture),
            headers, Encoding.UTF8.GetString(body.ToArray()));
    }

    private static async Task<string> ReadWireLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(one, cancellationToken);
            if (one[0] == '\n')
            {
                return Encoding.ASCII.GetString(line.ToArray()).TrimEnd('\r');
            }
            line.WriteByte(one[0]);
            line.Length.Should().BeLessThan(65_536, "a response header cannot grow without bound");
        }
    }


    private async Task<(string Token, Guid AccountId)> RegisterAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        using var response = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"size_{unique}",
            Email = $"size{unique}@example.com",
            Password = "TestPass123!",
            FirstName = "Body",
            LastName = "Limit",
        }, Json);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<RegisterResponse>>(Json);
        result.Should().NotBeNull();
        (result!.Data).Should().NotBeNull();
        return (result.Data!.Token.AccessToken, result.Data.Account.Id);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string path, string body, string? token, bool chunked = false, bool idempotent = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = chunked ? new ChunkedJsonContent(body) : new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (chunked)
        {
            request.Content.Headers.ContentLength.Should().BeNull();
            request.Headers.TransferEncodingChunked = true;
        }
        else
        {
            request.Content.Headers.ContentLength.Should().Be(Encoding.UTF8.GetByteCount(body));
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (idempotent)
        {
            request.Headers.Add(IdempotencyConstants.HeaderName, Guid.NewGuid().ToString());
        }
        return await _client.SendAsync(request);
    }

    private static string MoneyBody(string path, Guid accountId)
    {
        object body = path switch
        {
            DepositPath => new { accountId, amount = 250m, description = "Body limit proof" },
            "/api/transactions/withdraw" => new { accountId, amount = 1m },
            "/api/transfers" => new { fromAccountId = accountId, recipientAzureTag = "missing_payee", amount = 1m },
            "/api/transfers/internal" => new { fromAccountId = accountId, toAccountId = Guid.NewGuid(), amount = 1m },
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
        return JsonSerializer.Serialize(body, Json);
    }

    private static string OversizedBody(Guid accountId) => MoneyBody(DepositPath, accountId).PadRight(40_000);

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string errorCode)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
    }

    private static bool IsSource(LogEvent e, string source) =>
        e.Properties.TryGetValue("SourceContext", out var value) && value.Equals(new ScalarValue(source));

    private static bool HasEvent(LogEvent e, int id, string name) =>
        e.Properties.TryGetValue("EventId", out var value)
        && value is StructureValue structure
        && structure.Properties.Any(p => p.Name == "Id" && p.Value.Equals(new ScalarValue(id)))
        && structure.Properties.Any(p => p.Name == "Name" && p.Value.Equals(new ScalarValue(name)));

    private sealed class DebugLogFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Serilog:MinimumLevel:Override:Microsoft.AspNetCore", "Debug");

            // The address is configuration, in place before the host is built. UseKestrel(0) assigns
            // the port on the built host from this thread while the application's own thread goes
            // on to start the server: when that thread got there first, Kestrel went for its default
            // address, port 5000, and where that port was taken the start failed. Measured with
            // this thread held back three seconds: "Failed to bind to address
            // http://127.0.0.1:5000: address already in use", and StartServer threw
            // ObjectDisposedException. Held back the same way with the address set here, the host
            // came up on a free port.
            builder.UseUrls("http://127.0.0.1:0");
        }
    }

    private sealed class ChunkedJsonContent : HttpContent
    {
        private readonly byte[] _body;

        public ChunkedJsonContent(string body)
        {
            _body = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_body, 0, _body.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
